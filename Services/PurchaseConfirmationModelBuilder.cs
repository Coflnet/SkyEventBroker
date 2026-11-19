using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Coflnet.Sky.EventBroker.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Builds the language-resolved content of a purchase confirmation: only what a distance-contract
/// confirmation needs (order, price, payment, service period, subscription terms, withdrawal,
/// attached documents, trader identity). Hashes and descriptors stay in our records.
/// </summary>
internal static class PurchaseConfirmationModelBuilder
{
    private const string PrivacyUrl = "https://coflnet.com/privacy";
    private const string SupportEmail = "support@coflnet.com";
    internal const string Footer = "Coflnet GmbH, Dorfstraße 27a, 84163 Marklkofen, Germany — Local Court Landshut, HRB 13861 — support@coflnet.com";

    public static PurchaseConfirmationModel Build(PaymentEvent payment, bool german, LegalDocuments documents)
    {
        var sections = new List<EmailSection>
        {
            PriceDetails(payment, german),
            ServicePeriod(payment, german),
            EarlyPerformance(payment, german),
            OrderDetails(payment, german),
            Withdrawal(documents, german),
            AttachedDocuments(payment, documents, german),
            Contact(german)
        };
        return new PurchaseConfirmationModel(
            PurchaseConfirmationEmailService.Title(payment, german),
            Intro(payment, german),
            german ? "Bestellübersicht" : "Order overview",
            OverviewRows(payment, german),
            SubscriptionTermsSection.Notes(payment, german),
            sections.OfType<EmailSection>().ToList(),
            Footer);
    }

    private static List<(string Label, string Value)> OverviewRows(PaymentEvent payment, bool german)
    {
        var rows = new List<(string, string)> { (german ? "Produkt" : "Product", PaymentDisplayNames.Product(payment)) };
        var amount = TaxStatement.PaidAmount(payment, german);
        if (amount.Length > 0)
            rows.Add((german ? "Bezahlter Betrag" : "Amount paid", amount));
        if (payment.CoinAmount.HasValue)
            rows.Add(("CoflCoins", payment.CoinAmount.Value.ToString("0.##", CultureInfo.InvariantCulture)));
        rows.Add((german ? "Datum" : "Date", FormatUtc(payment.Timestamp)));
        var paidWith = PaidWith(payment, german);
        if (paidWith.Length > 0)
            rows.Add((german ? "Zahlung über" : "Paid with", paidWith));
        if (SubscriptionTermsSection.BillingPeriod(payment, german) is { } period)
            rows.Add(period);
        rows.Add((german ? "Transaktionsreferenz" : "Transaction reference", payment.PaymentProviderTransactionId));
        return rows;
    }

    private static string PaidWith(PaymentEvent payment, bool german)
    {
        var provider = PaymentDisplayNames.Provider(payment.PaymentProvider);
        var method = PaymentDisplayNames.Method(payment, german);
        return method.Length == 0 ? provider : provider.Length == 0 ? method : $"{provider} ({method})";
    }

    private static string Intro(PaymentEvent payment, bool german)
    {
        var seller = PurchaseConfirmationEmailService.SellerName(payment);
        if (PurchaseConfirmationEmailService.IsMerchantOfRecord(payment))
            return german
                ? $"Diese E-Mail bestätigt die Bereitstellung auf Ihrem Coflnet-Konto. {seller} war der beim Checkout ausgewiesene Verkäufer (Merchant of Record); dessen Beleg und Checkout-Bedingungen enthalten die verbindlichen Bestell-, Zahlungs- und Widerrufsangaben. Die beigefügten Coflnet-Dokumente betreffen nur das gesonderte SkyCofl-Nutzungsverhältnis und ändern den Verkauf durch {seller} nicht."
                : $"This email confirms fulfillment on your Coflnet account. {seller} was the seller (merchant of record) identified at checkout; its receipt and checkout terms contain the authoritative order, payment and withdrawal information. The attached Coflnet documents cover only the separate SkyCofl usage relationship and do not alter the sale by {seller}.";
        if (IsExpertConfig(payment))
            return german
                ? "Diese E-Mail bestätigt Zahlung und Bestellung bei der Coflnet GmbH. Die Lizenz an der bezeichneten Experten-Konfigurationsfassung und die verwaltete Aktualisierungsfunktion werden unmittelbar nach Zustellung dieser Bestätigung bereitgestellt."
                : "This email confirms payment and your order with Coflnet GmbH. The licence to the identified Expert Config version and the managed update facility are supplied immediately after delivery of this confirmation.";
        return german
            ? "Diese E-Mail bestätigt die Zahlung und Bereitstellung Ihres Vertrags mit der Coflnet GmbH."
            : "This email confirms payment and fulfillment of your contract with Coflnet GmbH.";
    }

    private static bool IsExpertConfig(PaymentEvent payment) =>
        string.Equals(payment.AgreementId, "expertMarketplace", StringComparison.Ordinal);

    private static EmailSection PriceDetails(PaymentEvent payment, bool german)
    {
        if (payment.GrossEurCents <= 0)
            return null;
        var taxName = payment.ConsumerRightsRegime == "US"
            ? german ? "Transaktionssteuer" : "transaction tax"
            : german ? "Umsatzsteuer" : "VAT";
        var regime = string.IsNullOrWhiteSpace(payment.ConsumerRightsRegime)
            ? ""
            : german
                ? $"; Verbraucherrechtsregime {payment.ConsumerRightsRegime}"
                : $"; {payment.ConsumerRightsRegime} consumer-rights regime";
        var gross = payment.GrossEurCents / 100m;
        var vat = payment.VatEurCents / 100m;
        var rate = payment.VatRateBasisPoints / 100m;
        var line = german
            ? $"Erfasster Bruttowert: EUR {gross:0.00} (einschließlich EUR {vat:0.00} {taxName}; {payment.TaxCountry}, {rate:0.##} %{regime})"
            : $"Recorded tax-inclusive value: EUR {gross:0.00} (including EUR {vat:0.00} {taxName}; {payment.TaxCountry}, {rate:0.##}%{regime})";
        return new EmailSection(german ? "Preis" : "Price", [line]);
    }

    private static EmailSection ServicePeriod(PaymentEvent payment, bool german)
    {
        if (!ShowsServicePeriod(payment))
            return null;
        return new EmailSection(
            german ? "Leistungszeitraum" : "Service period",
            [
                $"{(german ? "Leistungsbeginn" : "Service starts")}: {FormatUtc(payment.ServiceStartsAtUtc)}",
                $"{(german ? "Leistungsende" : "Service ends")}: {FormatUtc(payment.ServiceEndsAtUtc)}"
            ]);
    }

    private static EmailSection EarlyPerformance(PaymentEvent payment, bool german)
    {
        if (!ShowsServicePeriod(payment) || string.IsNullOrWhiteSpace(payment.DeclarationText))
            return null;
        return new EmailSection(
            german ? "Erklärung zum vorzeitigen Leistungsbeginn" : "Early-performance declaration",
            [$"{(german ? "Version" : "Version")}: {payment.DeclarationVersion}", payment.DeclarationText]);
    }

    private static bool ShowsServicePeriod(PaymentEvent payment) =>
        PurchaseConfirmationEmailService.IsServicePurchase(payment) && !IsExpertConfig(payment);

    private static EmailSection OrderDetails(PaymentEvent payment, bool german)
    {
        if (string.IsNullOrWhiteSpace(payment.OrderDetailsJson))
            return null;
        var json = JToken.Parse(payment.OrderDetailsJson).ToString(Formatting.Indented);
        return new EmailSection(german ? "Bestelldetails" : "Order details", [json], SectionStyle.Preformatted);
    }

    private static EmailSection Withdrawal(LegalDocuments documents, bool german) =>
        new(
            german ? "Widerruf" : "Withdrawal",
            [
                german
                    ? $"Widerrufsbelehrung ({documents.Withdrawal.Version}): {documents.Withdrawal.Url}"
                    : $"Withdrawal instructions ({documents.Withdrawal.Version}): {documents.Withdrawal.Url}",
                german
                    ? "Die Widerrufsbelehrung ist dieser E-Mail als Anhang beigefügt."
                    : "The withdrawal instructions are attached to this email."
            ]);

    private static EmailSection AttachedDocuments(PaymentEvent payment, LegalDocuments documents, bool german)
    {
        var lines = documents.AgreementDocuments
            .Select(document => $"{document.Title} ({document.Version})")
            .Append(german ? $"Widerrufsbelehrung ({documents.Withdrawal.Version})" : $"Withdrawal instructions ({documents.Withdrawal.Version})")
            .ToList();
        if (!string.IsNullOrWhiteSpace(payment.OrderDetailsJson))
            lines.Add(german ? "Bestelldetails (order-details.json)" : "Order details (order-details.json)");
        return new EmailSection(german ? "Anhänge" : "Attachments", lines, SectionStyle.Bullets);
    }

    private static EmailSection Contact(bool german) =>
        new(
            german ? "Datenschutz und Kontakt" : "Privacy and support",
            [
                $"{(german ? "Datenschutzerklärung" : "Privacy policy")}: {PrivacyUrl}",
                $"{(german ? "E-Mail" : "Email")}: {SupportEmail}"
            ]);

    private static string FormatUtc(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "";
}
