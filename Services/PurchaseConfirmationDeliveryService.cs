using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.EventBroker.Services;

public sealed class PurchaseConfirmationDeliveryService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly EventDbContext db;
    private readonly IPurchaseConfirmationEmailSender emailService;
    private readonly ILogger<PurchaseConfirmationDeliveryService> logger;

    public PurchaseConfirmationDeliveryService(
        EventDbContext db,
        IPurchaseConfirmationEmailSender emailService,
        ILogger<PurchaseConfirmationDeliveryService> logger)
    {
        this.db = db;
        this.emailService = emailService;
        this.logger = logger;
    }

    internal async Task<bool> ProcessOneAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var candidateId = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .Where(row => row.SentAt == null
                && row.NextAttemptAt <= now
                && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .OrderBy(row => row.CreatedAt)
            .Select(row => (long?)row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!candidateId.HasValue)
            return false;

        var leaseId = Guid.NewGuid();
        var claimed = await db.PurchaseConfirmationDeliveries
            .Where(row => row.Id == candidateId.Value
                && row.SentAt == null
                && row.NextAttemptAt <= now
                && (row.LeaseUntil == null || row.LeaseUntil <= now))
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.LeaseId, leaseId)
                .SetProperty(row => row.LeaseUntil, now + LeaseDuration)
                .SetProperty(row => row.Attempts, row => row.Attempts + 1),
                cancellationToken);
        if (claimed == 0)
            return true;

        var row = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .SingleAsync(item => item.Id == candidateId.Value
                && item.LeaseId == leaseId, cancellationToken);
        try
        {
            var payment = JsonConvert.DeserializeObject<PaymentEvent>(row.Payload)
                ?? throw new InvalidOperationException(
                    "Purchase confirmation payload is empty");
            await emailService.SendAsync(
                row.Recipient,
                row.Locale,
                payment,
                row.Reference);
            var sentAt = DateTime.UtcNow;
            await db.PurchaseConfirmationDeliveries
                .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.SentAt, sentAt)
                    .SetProperty(item => item.Recipient, (string)null)
                    .SetProperty(item => item.Locale, (string)null)
                    .SetProperty(item => item.Payload, (string)null)
                    .SetProperty(item => item.LeaseId, (Guid?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.LastError, (string)null),
                    cancellationToken);
        }
        catch (Exception exception)
        {
            var error = $"{exception.GetType().Name}: {exception.Message}";
            if (error.Length > 2000)
                error = error[..2000];
            var retryAt = now + TimeSpan.FromSeconds(
                Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 8))));
            await db.PurchaseConfirmationDeliveries
                .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.NextAttemptAt, retryAt)
                    .SetProperty(item => item.LeaseId, (Guid?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                    .SetProperty(item => item.LastError, error),
                    cancellationToken);
            logger.LogWarning(
                exception,
                "Could not send purchase confirmation {Reference}; retry scheduled",
                row.Reference);
        }

        return true;
    }
}
