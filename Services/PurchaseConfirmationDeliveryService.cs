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

    /// <summary>
    /// After this many attempts a permanently failing row (bad address, mismatched terms hash, ...)
    /// is parked (<see cref="PurchaseConfirmationDelivery.FailedAt"/>) instead of being retried forever.
    /// </summary>
    private const int MaxAttempts = 10;

    /// <summary>
    /// How many times the mark-as-sent update is retried after a successful send before giving up
    /// and leaving the lease to expire, so a transient write failure does not cause a resend.
    /// </summary>
    private const int MarkSentAttempts = 3;

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
                && row.FailedAt == null
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
                && row.FailedAt == null
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
        }
        catch (Exception exception)
        {
            // The send itself failed (SMTP down, bad address, terms-hash mismatch, ...). No email
            // went out, so it is safe to schedule a retry (or park after MaxAttempts) here.
            // Compute the backoff from "now" as of the failure, not the (possibly long stale, given
            // SmtpClient's ~100s default timeout) "now" captured before the send - otherwise the
            // backoff can already be in the past and become a no-op.
            var failedAt = DateTime.UtcNow;
            var error = $"{exception.GetType().Name}: {exception.Message}";
            if (error.Length > 2000)
                error = error[..2000];

            if (row.Attempts >= MaxAttempts)
            {
                await db.PurchaseConfirmationDeliveries
                    .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.FailedAt, failedAt)
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                        .SetProperty(item => item.LastError, error),
                        cancellationToken);
                logger.LogError(
                    exception,
                    "Purchase confirmation {Reference} failed permanently after {Attempts} attempts and was parked",
                    row.Reference,
                    row.Attempts);
                return true;
            }

            var retryAt = failedAt + TimeSpan.FromSeconds(
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
            return true;
        }

        // The email is out. From here a failure must NOT fall into the retry-scheduling path
        // above, or the customer risks getting emailed twice - if marking as sent keeps failing,
        // just let the lease expire instead.
        var sentAt = DateTime.UtcNow;
        for (var attempt = 1; attempt <= MarkSentAttempts; attempt++)
        {
            try
            {
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
                return true;
            }
            catch (Exception markException) when (attempt < MarkSentAttempts)
            {
                logger.LogWarning(
                    markException,
                    "Could not mark purchase confirmation {Reference} as sent (attempt {Attempt}/{MaxAttempt}); retrying",
                    row.Reference,
                    attempt,
                    MarkSentAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
            catch (Exception markException)
            {
                logger.LogCritical(
                    markException,
                    "Purchase confirmation {Reference} was emailed but could not be marked as sent after {Attempts} attempts; leaving the lease to expire instead of risking a duplicate send",
                    row.Reference,
                    MarkSentAttempts);
            }
        }

        return true;
    }
}
