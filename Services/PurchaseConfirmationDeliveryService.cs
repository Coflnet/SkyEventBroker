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
    private static readonly Prometheus.Counter parkedCount = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_purchase_confirmation_parked_total",
        "Purchase confirmations parked after exhausting their delivery retries");
    private static readonly Prometheus.Counter sentCount = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_purchase_confirmation_sent_total",
        "Purchase confirmation emails successfully sent");
    private static readonly Prometheus.Counter reminderSentCount = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_renewal_reminder_sent_total",
        "Subscription renewal reminder emails successfully sent");
    private static readonly Prometheus.Counter reminderParkedCount = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_renewal_reminder_parked_total",
        "Renewal reminders parked after exhausting their delivery retries");
    private static readonly Prometheus.Counter reminderSkippedCount = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_renewal_reminder_skipped_total",
        "Subscription renewal reminders not sent because the term had already ended");
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
    private readonly Coflnet.Sky.Indexer.Client.Api.IUserApi userApi;
    private readonly ILogger<PurchaseConfirmationDeliveryService> logger;

    public PurchaseConfirmationDeliveryService(
        EventDbContext db,
        IPurchaseConfirmationEmailSender emailService,
        Coflnet.Sky.Indexer.Client.Api.IUserApi userApi,
        ILogger<PurchaseConfirmationDeliveryService> logger)
    {
        this.db = db;
        this.emailService = emailService;
        this.userApi = userApi;
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

        var isReminder = false;
        var row = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .SingleAsync(item => item.Id == candidateId.Value
                && item.LeaseId == leaseId, cancellationToken);
        try
        {
            var payment = JsonConvert.DeserializeObject<PaymentEvent>(row.Payload)
                ?? throw new InvalidOperationException(
                    "Purchase confirmation payload is empty");
            isReminder = PurchaseConfirmationEmailService.IsRenewalReminder(payment);
            if (IsOutdatedReminder(payment))
            {
                await MarkSkippedAsync(row, leaseId, cancellationToken);
                return true;
            }
            // NewPayment enqueues rows with a null Recipient whenever the indexer lookup could
            // not be completed inline (see MessageService.TryGetIndexerEmail) so that the
            // purchase itself is never gated on the email address. Resolve it here instead; if
            // that still fails, throwing below routes through the existing retry/backoff/park
            // machinery rather than a parallel one.
            var recipient = row.Recipient;
            if (string.IsNullOrWhiteSpace(recipient))
            {
                string resolvedEmail;
                try
                {
                    resolvedEmail = (await userApi.UserEmailGetAsync(payment.UserId))?.Email;
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Could not resolve a recipient email for {Kind(isReminder)} {row.Reference} via the indexer",
                        exception);
                }
                if (string.IsNullOrWhiteSpace(resolvedEmail))
                    throw new InvalidOperationException(
                        $"No account email found for {Kind(isReminder)} {row.Reference}");
                recipient = resolvedEmail;
            }
            await emailService.SendAsync(
                recipient,
                row.Locale,
                payment,
                row.Reference,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // The send itself failed (SMTP down, bad address, terms-hash mismatch, ...). No email
            // went out, so it is safe to schedule a retry (or park after MaxAttempts) here.
            // Compute the backoff from "now" as of the failure, not the (possibly long stale, given
            // the send timeout of PurchaseConfirmationEmailService) "now" captured before the send - otherwise the
            // backoff can already be in the past and become a no-op.
            var failedAt = DateTime.UtcNow;
            var error = $"{exception.GetType().Name}: {exception.Message}";
            if (error.Length > 2000)
                error = error[..2000];

            if (row.Attempts >= MaxAttempts)
            {
                var parked = await db.PurchaseConfirmationDeliveries
                    .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(item => item.FailedAt, failedAt)
                        .SetProperty(item => item.LeaseId, (Guid?)null)
                        .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                        .SetProperty(item => item.LastError, error),
                        cancellationToken);
                if (parked > 0)
                    (isReminder ? reminderParkedCount : parkedCount).Inc();
                logger.LogError(
                    exception,
                    "{Kind} {Reference} failed permanently after {Attempts} attempts and was parked",
                    Kind(isReminder),
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
                "Could not send {Kind} {Reference}; retry scheduled",
                Kind(isReminder),
                row.Reference);
            return true;
        }

        // The email is out. From here a failure must NOT fall into the retry-scheduling path
        // above, or the customer risks getting emailed twice - if marking as sent keeps failing,
        // just let the lease expire instead.
        (isReminder ? reminderSentCount : sentCount).Inc();
        logger.LogInformation(
            "Sent {Kind} {Reference} on attempt {Attempts}",
            Kind(isReminder),
            row.Reference,
            row.Attempts);
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
                        // the email already went out: a shutdown must not stop recording that, or the next pod resends it
                        CancellationToken.None);
                return true;
            }
            catch (Exception markException) when (attempt < MarkSentAttempts)
            {
                logger.LogWarning(
                    markException,
                    "Could not mark {Kind} {Reference} as sent (attempt {Attempt}/{MaxAttempt}); retrying",
                    Kind(isReminder),
                    row.Reference,
                    attempt,
                    MarkSentAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), CancellationToken.None);
            }
            catch (Exception markException)
            {
                logger.LogCritical(
                    markException,
                    "{Kind} {Reference} was emailed but could not be marked as sent after {Attempts} attempts; leaving the lease to expire instead of risking a duplicate send",
                    Kind(isReminder),
                    row.Reference,
                    MarkSentAttempts);
            }
        }

        return true;
    }

    /// <summary>A reminder whose term already ended by the time it would go out is obsolete: the 4-week billing has started.</summary>
    private static bool IsOutdatedReminder(PaymentEvent payment) =>
        PurchaseConfirmationEmailService.IsRenewalReminder(payment)
        && payment.SubscriptionTermEndsAt.HasValue
        && payment.SubscriptionTermEndsAt.Value.ToUniversalTime() <= DateTime.UtcNow;

    /// <summary>Closes the row without sending, keeping it as the dedupe record with the reason in LastError.</summary>
    private async Task MarkSkippedAsync(PurchaseConfirmationDelivery row, Guid leaseId, CancellationToken cancellationToken)
    {
        await db.PurchaseConfirmationDeliveries
            .Where(item => item.Id == row.Id && item.LeaseId == leaseId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.SentAt, DateTime.UtcNow)
                .SetProperty(item => item.Recipient, (string)null)
                .SetProperty(item => item.Locale, (string)null)
                .SetProperty(item => item.Payload, (string)null)
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntil, (DateTime?)null)
                .SetProperty(item => item.LastError, "skipped: the subscription term had already ended"),
                cancellationToken);
        reminderSkippedCount.Inc();
        logger.LogInformation("Renewal reminder {Reference} skipped, its term had already ended", row.Reference);
    }

    private static string Kind(bool isReminder) => isReminder ? "renewal reminder" : "purchase confirmation";
}
