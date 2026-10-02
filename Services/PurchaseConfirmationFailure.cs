using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net.Http;
using System.Net.Mail;
using System.Linq;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.EventBroker.Services;

public enum PurchaseConfirmationFailureKind
{
    /// <summary>Retrying may succeed (SMTP down, documents still loading, ...).</summary>
    Transient,
    /// <summary>Retrying can never succeed; the row is parked right away.</summary>
    Permanent
}

/// <summary>
/// Carries a fixed <see cref="PurchaseConfirmationFailure"/> reason from the throw site, so the
/// classification never depends on parsing English messages.
/// </summary>
internal sealed class PurchaseConfirmationException : InvalidOperationException
{
    public PurchaseConfirmationException(string reason, string message, Exception inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

internal static class PurchaseConfirmationFailure
{
    public const string AgreementMismatch = "agreement_mismatch";
    public const string TermsMismatch = "terms_mismatch";
    public const string WithdrawalMismatch = "withdrawal_mismatch";
    public const string DocumentsNotLoaded = "documents_not_loaded";
    public const string NoRecipient = "no_recipient";
    public const string RecipientLookupFailed = "recipient_lookup_failed";
    public const string SmtpNotConfigured = "smtp_not_configured";
    public const string SmtpRejectedRecipient = "smtp_rejected_recipient";
    public const string SmtpError = "smtp_error";
    public const string Database = "database";
    public const string Other = "other";

    /// <summary>The complete, low-cardinality reason set (metric label values).</summary>
    public static readonly IReadOnlyList<string> Reasons =
    [
        AgreementMismatch, TermsMismatch, WithdrawalMismatch, DocumentsNotLoaded, NoRecipient,
        RecipientLookupFailed, SmtpNotConfigured, SmtpRejectedRecipient, SmtpError, Database, Other
    ];

    public static (string Reason, PurchaseConfirmationFailureKind Kind) Classify(Exception exception)
    {
        // The most specific exception in the chain wins: walk from the outside in.
        for (var current = exception; current != null; current = current.InnerException)
        {
            var reason = ClassifySingle(current);
            if (reason != null)
                return (reason, KindOf(reason));
        }
        return (Other, PurchaseConfirmationFailureKind.Transient);
    }

    /// <summary>
    /// The persisted LastError form. The reason is a prefix (no extra column, so no manual
    /// production migration is needed): <c>[reason] Type: message</c>.
    /// </summary>
    public static string FormatError(string reason, Exception exception)
    {
        var error = $"[{reason}] {exception.GetType().Name}: {exception.Message}";
        return error.Length > 2000 ? error[..2000] : error;
    }

    /// <summary>Reads the reason back from a <see cref="FormatError"/> string (null for old rows).</summary>
    public static string ParseReason(string lastError)
    {
        if (lastError == null || !lastError.StartsWith('['))
            return null;
        var end = lastError.IndexOf(']');
        if (end < 2)
            return null;
        var reason = lastError[1..end];
        return Reasons.Contains(reason) ? reason : null;
    }

    private static PurchaseConfirmationFailureKind KindOf(string reason) => reason switch
    {
        AgreementMismatch or TermsMismatch or WithdrawalMismatch or SmtpRejectedRecipient
            => PurchaseConfirmationFailureKind.Permanent,
        _ => PurchaseConfirmationFailureKind.Transient
    };

    private static string ClassifySingle(Exception exception)
    {
        switch (exception)
        {
            case PurchaseConfirmationException tagged:
                return tagged.Reason;
            // must precede SmtpException, its base class. A 4xx reply (mailbox busy, ...) is temporary.
            case SmtpFailedRecipientException recipient:
                return (int)recipient.StatusCode >= 500 ? SmtpRejectedRecipient : SmtpError;
            case SmtpException smtp:
                return smtp.StatusCode is SmtpStatusCode.MailboxUnavailable
                    or SmtpStatusCode.UserNotLocalTryAlternatePath
                    or SmtpStatusCode.MailboxNameNotAllowed
                    ? SmtpRejectedRecipient
                    : SmtpError;
            // The indexer client is the only HTTP client on this path.
            case Coflnet.Sky.Indexer.Client.Client.ApiException api:
                return api.ErrorCode == 404 ? NoRecipient : RecipientLookupFailed;
            case HttpRequestException:
                return RecipientLookupFailed;
            case DbException or DbUpdateException:
                return Database;
            case SocketException or IOException or TimeoutException or OperationCanceledException:
                return SmtpError;
            default:
                return null;
        }
    }
}

internal static class PurchaseConfirmationMetrics
{
    public static readonly Prometheus.Counter Parked = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_purchase_confirmation_parked_total",
        "Purchase confirmations parked (need an operator), by failure reason",
        new Prometheus.CounterConfiguration { LabelNames = ["reason"] });

    public static readonly Prometheus.Counter AttemptFailed = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_purchase_confirmation_attempt_failed_total",
        "Failed purchase confirmation delivery attempts, by failure reason",
        new Prometheus.CounterConfiguration { LabelNames = ["reason"] });

    public static readonly Prometheus.Counter Sent = Prometheus.Metrics.CreateCounter(
        "sky_eventbroker_purchase_confirmation_sent_total",
        "Purchase confirmations emailed");

    /// <summary>Creates every labelled series at 0 so increase() sees the first real failure.</summary>
    public static void Initialize()
    {
        foreach (var reason in PurchaseConfirmationFailure.Reasons)
        {
            Parked.WithLabels(reason);
            AttemptFailed.WithLabels(reason);
        }
    }
}
