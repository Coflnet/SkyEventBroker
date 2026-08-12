using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.EventBroker.Services;

public interface IPurchaseConfirmationEmailSender
{
    Task SendAsync(
        string toEmail,
        string locale,
        PaymentEvent payment,
        string confirmationReference = null);
}

public class PurchaseConfirmationEmailService : IPurchaseConfirmationEmailSender
{
    private const string PrivacyUrl = "https://coflnet.com/privacy";
    private const string Footer = "Coflnet GmbH, Dorfstraße 27a, 84163 Marklkofen, Germany — Local Court Landshut, HRB 13861 — support@coflnet.com";

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
        string confirmationReference = null)
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
        var from = config["SMTP_FROM"] ?? "noreply@coflnet.com";
        var documents = legalDocuments.Get(locale);
        ValidateAgreement(payment, documents);

        var text = BuildContent(payment, locale, documents);
        var subject = IsMerchantOfRecord(payment.PaymentProvider)
            ? LocaleHelper.IsGerman(locale)
                ? "Bereitstellungsbestätigung — Coflnet"
                : "Fulfillment confirmation — Coflnet"
            : LocaleHelper.IsGerman(locale)
                ? "Kaufbestätigung — Coflnet"
                : "Purchase confirmation — Coflnet";

        try
        {
            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(user, password),
                EnableSsl = true
            };
            using var message = new MailMessage(from, toEmail, subject, text);
            if (!string.IsNullOrWhiteSpace(confirmationReference))
                message.Headers["Message-ID"] =
                    $"<{confirmationReference}@mail.coflnet.com>";
            message.Attachments.Add(new Attachment(
                new MemoryStream(documents.AgreementDescriptor),
                $"skycofl-agreement-{documents.AgreementHash}.json",
                "application/json"));
            foreach (var document in documents.AgreementDocuments)
                message.Attachments.Add(document.CreateAttachment());
            message.Attachments.Add(documents.Withdrawal.CreateAttachment());
            await client.SendMailAsync(message);
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

    internal static string BuildContent(
        PaymentEvent payment,
        string locale,
        LegalDocuments documents)
    {
        var german = LocaleHelper.IsGerman(locale);
        var provider = payment.PaymentProvider ?? "";
        var merchantOfRecord = IsMerchantOfRecord(provider);
        var amount = payment.PayedAmount > 0
            ? $"{payment.PayedAmount.ToString("0.00##", CultureInfo.InvariantCulture)} {payment.Currency}".Trim()
            : "";
        var timestamp = payment.Timestamp.ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var documentList = string.Join(
            "\n",
            documents.AgreementDocuments.Select(document =>
                $"- {document.Title} ({document.Version}): {document.Url} [SHA-256 {document.Hash}]"));
        var serviceDetails = IsServicePurchase(payment)
            ? german
                ? $"""

LEISTUNGSZEITRAUM
-----------------
Leistungsbeginn: {FormatUtc(payment.ServiceStartsAtUtc)}
Leistungsende: {FormatUtc(payment.ServiceEndsAtUtc)}
{(string.IsNullOrWhiteSpace(payment.DeclarationText) ? "" : $"""

ERKLÄRUNG ZUM VORZEITIGEN LEISTUNGSBEGINN
------------------------------------------
Version: {payment.DeclarationVersion}
{payment.DeclarationText}
""")}
"""
                : $"""

SERVICE PERIOD
--------------
Service starts: {FormatUtc(payment.ServiceStartsAtUtc)}
Service ends: {FormatUtc(payment.ServiceEndsAtUtc)}
{(string.IsNullOrWhiteSpace(payment.DeclarationText) ? "" : $"""

EARLY-PERFORMANCE DECLARATION
-----------------------------
Version: {payment.DeclarationVersion}
{payment.DeclarationText}
""")}
"""
            : "";

        if (german)
            return $"""
{(merchantOfRecord ? "Bereitstellungsbestätigung" : "Kaufbestätigung")}
===================================================

{(merchantOfRecord
    ? $"Diese E-Mail bestätigt die Bereitstellung auf Ihrem Coflnet-Konto. {provider} war der beim Checkout ausgewiesene Verkäufer (Merchant of Record); dessen Beleg und Checkout-Bedingungen enthalten die verbindlichen Bestell-, Zahlungs- und Widerrufsangaben. Die beigefügten Coflnet-Dokumente betreffen nur das gesonderte SkyCofl-Nutzungsverhältnis und ändern den Verkauf durch {provider} nicht."
    : "Diese E-Mail bestätigt die Zahlung und Bereitstellung Ihres Vertrags mit der Coflnet GmbH.")}

BESTELLUNG
----------
Produkt: {payment.ProductId}
Transaktionsreferenz: {payment.PaymentProviderTransactionId}
Datum/Uhrzeit: {timestamp}

ZAHLUNG
-------
Zahlungsanbieter: {provider}
{(amount.Length == 0 ? "" : $"Betrag: {amount}\n")}{(payment.CoinAmount.HasValue ? $"CoflCoins: {payment.CoinAmount.Value.ToString("0.##", CultureInfo.InvariantCulture)}\n" : "")}{(string.IsNullOrWhiteSpace(payment.PaymentMethod) ? "" : $"Zahlungsart: {payment.PaymentMethod}\n")}
{serviceDetails}

VEREINBARUNG
-------------
Erfasste Vereinbarung: {documents.AgreementId}
Vereinbarungs-Hash: {documents.AgreementHash}
Vereinbarungsdeskriptor: {documents.AgreementUrl}

Beigefügte Vereinbarungsdokumente:
{documentList}

WIDERRUF
--------
Widerrufsbelehrung ({documents.Withdrawal.Version}):
{documents.Withdrawal.Url}
SHA-256: {documents.Withdrawal.Hash}

DATENSCHUTZ UND KONTAKT
-----------------------
Datenschutzerklärung: {PrivacyUrl}
E-Mail: support@coflnet.com

{Footer}
""";

        return $"""
{(merchantOfRecord ? "Fulfillment confirmation" : "Purchase confirmation")}
===================================================

{(merchantOfRecord
    ? $"This email confirms fulfillment on your Coflnet account. {provider} was the seller (merchant of record) identified at checkout; its receipt and checkout terms contain the authoritative order, payment and withdrawal information. The attached Coflnet documents cover only the separate SkyCofl usage relationship and do not alter the sale by {provider}."
    : "This email confirms payment and fulfillment of your contract with Coflnet GmbH.")}

ORDER
-----
Product: {payment.ProductId}
Transaction reference: {payment.PaymentProviderTransactionId}
Date/time: {timestamp}

PAYMENT
-------
Payment provider: {provider}
{(amount.Length == 0 ? "" : $"Amount: {amount}\n")}{(payment.CoinAmount.HasValue ? $"CoflCoins: {payment.CoinAmount.Value.ToString("0.##", CultureInfo.InvariantCulture)}\n" : "")}{(string.IsNullOrWhiteSpace(payment.PaymentMethod) ? "" : $"Payment method: {payment.PaymentMethod}\n")}
{serviceDetails}

AGREEMENT
---------
Recorded agreement: {documents.AgreementId}
Agreement hash: {documents.AgreementHash}
Agreement descriptor: {documents.AgreementUrl}

Attached agreement documents:
{documentList}

WITHDRAWAL
----------
Withdrawal instructions ({documents.Withdrawal.Version}):
{documents.Withdrawal.Url}
SHA-256: {documents.Withdrawal.Hash}

PRIVACY AND SUPPORT
-------------------
Privacy policy: {PrivacyUrl}
Email: support@coflnet.com

{Footer}
""";
    }

    private static bool IsMerchantOfRecord(string provider) =>
        string.Equals(provider, "lemonsqueezy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "Google Play", StringComparison.OrdinalIgnoreCase);

    private static bool IsServicePurchase(PaymentEvent payment) =>
        string.Equals(
            payment.ConfirmationType,
            "service_purchase",
            StringComparison.OrdinalIgnoreCase);

    private static string FormatUtc(DateTime? value) =>
        value?.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture) ?? "";
}
