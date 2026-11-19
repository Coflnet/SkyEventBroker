using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.EventBroker.Services;

public interface IPurchaseConfirmationEmailSender
{
    Task SendAsync(
        string toEmail,
        string locale,
        PaymentEvent payment,
        string confirmationReference = null,
        CancellationToken cancellationToken = default);
}

public class PurchaseConfirmationEmailService : IPurchaseConfirmationEmailSender
{
    private const string DefaultFrom = "noreply@coflnet.com";
    private const string DefaultFromName = "Coflnet";
    private const string DefaultReplyTo = "support@coflnet.com";

    /// <summary>
    /// SmtpClient.Timeout only applies to synchronous sends, so async sends are bounded by this
    /// instead; a stalled SMTP server would otherwise block every confirmation silently.
    /// Generous, since a server may take a while to accept the message after its final ".",
    /// and a timeout there means a duplicate on retry, but below the 5 minute delivery lease.
    /// </summary>
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromMinutes(3);

    internal TimeSpan SendTimeout { get; set; } = DefaultSendTimeout;

    private readonly IConfiguration config;
    private readonly ILogger<PurchaseConfirmationEmailService> logger;
    private readonly LegalDocumentProvider legalDocuments;

    public PurchaseConfirmationEmailService(
        IConfiguration config,
        ILogger<PurchaseConfirmationEmailService> logger,
        LegalDocumentProvider legalDocuments)
    {
        this.config = config;
        this.logger = logger;
        this.legalDocuments = legalDocuments;
    }

    public async Task SendAsync(
        string toEmail,
        string locale,
        PaymentEvent payment,
        string confirmationReference = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        ArgumentNullException.ThrowIfNull(payment);

        var host = config["SMTP_HOST"];
        var user = config["SMTP_USER"];
        var password = config["SMTP_PASSWORD"];
        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(user)
            || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "SMTP is not configured (SMTP_HOST/SMTP_USER/SMTP_PASSWORD missing)");

        var port = int.TryParse(config["SMTP_PORT"], out var parsedPort)
            ? parsedPort
            : 587;
        if (IsRenewalReminder(payment))
        {
            await SendReminderAsync(host, port, new NetworkCredential(user, password), toEmail, locale, payment, confirmationReference, cancellationToken);
            return;
        }
        var documents = legalDocuments.Get(
            locale,
            string.IsNullOrWhiteSpace(payment.AgreementId)
                ? "skycofl"
                : payment.AgreementId);
        ValidateAgreement(payment, documents);

        try
        {
            using var message = CreateMessage(
                config, toEmail, locale, payment, documents, confirmationReference);
            await SendWithTimeoutAsync(
                host, port, new NetworkCredential(user, password), message, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to send purchase confirmation for payment {PaymentId}",
                payment.PaymentProviderTransactionId);
            throw;
        }
    }

    private async Task SendReminderAsync(
        string host,
        int port,
        NetworkCredential credentials,
        string toEmail,
        string locale,
        PaymentEvent payment,
        string reference,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = CreateReminderMessage(config, toEmail, locale, payment, reference);
            await SendWithTimeoutAsync(host, port, credentials, message, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to send renewal reminder {Reference}",
                reference);
            throw;
        }
    }

    /// <summary>The reminder: plain text with an HTML alternative, no legal attachments.</summary>
    internal static MailMessage CreateReminderMessage(
        IConfiguration config,
        string toEmail,
        string locale,
        PaymentEvent payment,
        string reference = null)
    {
        var german = LocaleHelper.IsGerman(locale);
        var model = RenewalReminderModelBuilder.Build(payment, german);
        var message = new MailMessage(SenderAddress(config), new MailAddress(toEmail))
        {
            Subject = RenewalReminderModelBuilder.Subject(payment, german),
            Body = PurchaseConfirmationRenderer.ToText(model),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            PurchaseConfirmationRenderer.ToHtml(model, german ? "de" : "en"), Encoding.UTF8, "text/html"));
        message.ReplyToList.Add(ReplyToAddress(config));
        if (!string.IsNullOrWhiteSpace(reference))
            message.Headers["Message-ID"] = $"<{reference}@mail.coflnet.com>";
        return message;
    }

    /// <summary>The From address with a display name; mail without one is far more likely to be filed as spam.</summary>
    internal static MailAddress SenderAddress(IConfiguration config) =>
        new(
            config["SMTP_FROM"] is { Length: > 0 } from ? from : DefaultFrom,
            config["SMTP_FROM_NAME"] is { Length: > 0 } name ? name : DefaultFromName);

    /// <summary>Where replies go, so answering the confirmation reaches support instead of the unmonitored sender.</summary>
    internal static MailAddress ReplyToAddress(IConfiguration config) =>
        new(config["SMTP_REPLY_TO"] is { Length: > 0 } replyTo ? replyTo : DefaultReplyTo);

    /// <summary>
    /// Sends the message, failing with a <see cref="TimeoutException"/> when the server does not
    /// finish within <see cref="SendTimeout"/>. The caller's token is linked, so its
    /// cancellation still surfaces as an <see cref="OperationCanceledException"/>.
    /// </summary>
    internal async Task SendWithTimeoutAsync(
        string host,
        int port,
        NetworkCredential credentials,
        MailMessage message,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(host, port)
        {
            Credentials = credentials,
            EnableSsl = true
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SMTP send did not complete within {SendTimeout.TotalSeconds:0.##}s");
        }
    }

    /// <summary>
    /// Confirms the recorded agreement root (or a legacy Terms hash) and
    /// withdrawal identity match the documents that will be attached.
    /// </summary>
    internal static void ValidateAgreement(PaymentEvent payment, LegalDocuments documents)
    {
        if (!string.IsNullOrWhiteSpace(payment.AgreementId)
            || !string.IsNullOrWhiteSpace(payment.AgreementHash))
        {
            if (!string.Equals(
                    payment.AgreementId,
                    documents.AgreementId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    payment.AgreementHash,
                    documents.AgreementHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The purchase's accepted agreement root does not match the loaded SkyCofl agreement.");
        }
        else if (!string.IsNullOrWhiteSpace(payment.TermsAcceptanceHash))
        {
            var terms = documents.AgreementDocuments.FirstOrDefault(
                document => string.Equals(document.Key, "terms", StringComparison.OrdinalIgnoreCase));
            if (terms == null
                || !string.Equals(
                    payment.TermsAcceptanceHash,
                    terms.AcceptanceHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The purchase's accepted terms hash does not match the loaded SkyCofl terms document.");
        }
        if (!string.IsNullOrWhiteSpace(payment.WithdrawalVersion)
            && !string.Equals(
                payment.WithdrawalVersion,
                documents.Withdrawal.Version,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The purchase's withdrawal instructions version does not match the loaded SkyCofl withdrawal document.");
        if (!string.IsNullOrWhiteSpace(payment.WithdrawalSha256)
            && !string.Equals(
                payment.WithdrawalSha256,
                documents.Withdrawal.Hash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The purchase's withdrawal instructions hash does not match the loaded SkyCofl withdrawal document.");
    }

    internal static string BuildContent(PaymentEvent payment, string locale, LegalDocuments documents) =>
        PurchaseConfirmationRenderer.ToText(Model(payment, locale, documents));

    internal static string BuildHtml(PaymentEvent payment, string locale, LegalDocuments documents) =>
        PurchaseConfirmationRenderer.ToHtml(Model(payment, locale, documents), LocaleHelper.IsGerman(locale) ? "de" : "en");

    private static PurchaseConfirmationModel Model(PaymentEvent payment, string locale, LegalDocuments documents) =>
        PurchaseConfirmationModelBuilder.Build(payment, LocaleHelper.IsGerman(locale), documents);

    /// <summary>
    /// The message: plain text with an HTML alternative, the applicable legal documents attached as the durable medium.
    /// </summary>
    internal static MailMessage CreateMessage(
        IConfiguration config,
        string toEmail,
        string locale,
        PaymentEvent payment,
        LegalDocuments documents,
        string confirmationReference = null)
    {
        var message = new MailMessage(SenderAddress(config), new MailAddress(toEmail))
        {
            Subject = $"{Title(payment, LocaleHelper.IsGerman(locale))} — Coflnet",
            Body = BuildContent(payment, locale, documents),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            BuildHtml(payment, locale, documents), Encoding.UTF8, "text/html"));
        message.ReplyToList.Add(ReplyToAddress(config));
        if (!string.IsNullOrWhiteSpace(confirmationReference))
            message.Headers["Message-ID"] = $"<{confirmationReference}@mail.coflnet.com>";
        AddAttachments(message, payment, documents);
        return message;
    }

    private static void AddAttachments(MailMessage message, PaymentEvent payment, LegalDocuments documents)
    {
        foreach (var document in documents.AgreementDocuments)
            message.Attachments.Add(document.CreateAttachment());
        message.Attachments.Add(documents.Withdrawal.CreateAttachment());
        if (string.IsNullOrWhiteSpace(payment.OrderDetailsJson))
            return;
        message.Attachments.Add(new Attachment(
            new MemoryStream(Encoding.UTF8.GetBytes(payment.OrderDetailsJson)),
            "order-details.json",
            "application/json"));
    }

    internal const string ManagedPaymentsMethod = "stripe_managed_payments";

    /// <summary>
    /// Lemon Squeezy, Google Play and Stripe Managed Payments (Payments sends provider "stripe" with
    /// payment method "stripe_managed_payments") sell as merchant of record; direct Stripe/PayPal sales are Coflnet's.
    /// </summary>
    internal static bool IsMerchantOfRecord(PaymentEvent payment) =>
        IsMerchantOfRecord(payment.PaymentProvider)
        || string.Equals(payment.PaymentMethod, ManagedPaymentsMethod, StringComparison.OrdinalIgnoreCase);

    private static bool IsMerchantOfRecord(string provider) =>
        string.Equals(provider, "lemonsqueezy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "Google Play", StringComparison.OrdinalIgnoreCase);

    internal static string SellerName(PaymentEvent payment) =>
        string.Equals(payment.PaymentMethod, ManagedPaymentsMethod, StringComparison.OrdinalIgnoreCase)
            ? "Sold through Link, LLC (Stripe Managed Payments)"
            : PaymentDisplayNames.Provider(payment.PaymentProvider);

    internal static string Title(PaymentEvent payment, bool german)
    {
        var renewal = payment.SubscriptionIsRenewal == true || payment.SubscriptionIsExtension == true;
        if (!IsMerchantOfRecord(payment))
            return renewal
                ? german ? "Verlängerungsbestätigung" : "Renewal confirmation"
                : german ? "Kaufbestätigung" : "Purchase confirmation";
        var title = german ? "Bereitstellungsbestätigung" : "Fulfillment confirmation";
        if (!renewal)
            return title;
        return german ? $"{title} — Verlängerung" : $"{title} — renewal";
    }

    internal const string RenewalReminderType = "subscription_renewal_reminder";

    /// <summary>A reminder that a term ends soon. It is an email only, never a payment.</summary>
    internal static bool IsRenewalReminder(PaymentEvent payment) =>
        string.Equals(payment?.ConfirmationType?.Trim(), RenewalReminderType, StringComparison.OrdinalIgnoreCase);

    internal static bool IsServicePurchase(PaymentEvent payment) =>
        string.Equals(
            payment.ConfirmationType,
            "service_purchase",
            StringComparison.OrdinalIgnoreCase);

}
