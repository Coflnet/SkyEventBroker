using System;
using System.Globalization;
using Coflnet.Sky.EventBroker.Models;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// What a purchase confirmation states about tax next to an amount (PAngV, Art. 246a EGBGB: the total price including taxes).
/// Only what the event makes true: an amount without known tax treatment gets no statement.
/// </summary>
internal static class TaxStatement
{
    public const string Included = "included";
    public const string Net = "net";
    public const string None = "none";
    private const string LemonSqueezy = "lemonsqueezy";

    /// <summary>The paid amount with its tax statement, e.g. "11.53 EUR (inkl. 19 % MwSt.: 1.84 EUR)"; empty without an amount.</summary>
    public static string PaidAmount(PaymentEvent payment, bool german)
    {
        if (payment.PayedAmount <= 0)
            return "";
        var amount = Money((decimal)payment.PayedAmount, payment.Currency);
        return WithStatement(amount, PaidStatement(payment, german));
    }

    /// <summary>The renewal amount with its tax statement, e.g. "11.53 EUR (inkl. 19 % MwSt.)"; empty without an amount.</summary>
    public static string RenewalAmount(PaymentEvent payment, bool german)
    {
        if (payment.SubscriptionRenewalAmount is not > 0)
            return "";
        var amount = Money(payment.SubscriptionRenewalAmount.Value, payment.Currency);
        return WithStatement(amount, Statement(payment.RenewalTaxTreatment, payment, null, german));
    }

    private static string PaidStatement(PaymentEvent payment, bool german)
    {
        if (payment.TaxTreatment == null)
            return Fallback(payment, german);
        if (IsChargeWithUnreportedTax(payment))
            return ReceiptStatement(payment, german);
        return Statement(payment.TaxTreatment, payment, payment.TaxAmount, german);
    }

    /// <summary>
    /// A merchant of record's whole charge whose tax we do not know (e.g. Managed Payments without tax on the invoice):
    /// it includes whatever tax was due, possibly none, so only its receipt can name it.
    /// </summary>
    private static bool IsChargeWithUnreportedTax(PaymentEvent payment) =>
        payment.TaxTreatment == Included && payment.TaxAmount is not > 0 && payment.VatRateBasisPoints <= 0
        && PurchaseConfirmationEmailService.IsMerchantOfRecord(payment);

    private static string Statement(string treatment, PaymentEvent payment, decimal? taxAmount, bool german) => treatment switch
    {
        Included => IncludedStatement(payment, taxAmount, german),
        Net => german ? "zzgl. anfallender Steuern" : "plus applicable taxes",
        _ => ""
    };

    private static string IncludedStatement(PaymentEvent payment, decimal? taxAmount, bool german)
    {
        var statement = (german ? "inkl. " : "incl. ") + TaxName(payment, german);
        return taxAmount is > 0 ? $"{statement}: {Money(taxAmount.Value, payment.Currency)}" : statement;
    }

    /// <summary>"19 % MwSt." with a known rate, "MwSt." for Coflnet's own sales, "Steuern" for a merchant of record's tax.</summary>
    private static string TaxName(PaymentEvent payment, bool german)
    {
        if (payment.VatRateBasisPoints > 0)
            return german ? $"{Rate(payment)} % MwSt." : $"{Rate(payment)}% VAT";
        if (!PurchaseConfirmationEmailService.IsMerchantOfRecord(payment))
            return german ? "MwSt." : "VAT";
        return german ? "Steuern" : "taxes";
    }

    /// <summary>
    /// Events without tax treatment: the paid amount of Lemon Squeezy (its order total) and Managed Payments (the invoice's
    /// amount_paid) is the whole charge including the taxes of the merchant of record; for everything else nothing is known
    /// (Google Play events carry our list price, not the charge), so nothing is claimed.
    /// </summary>
    private static string Fallback(PaymentEvent payment, bool german) =>
        IsLemonSqueezy(payment) || IsManagedPayments(payment) ? ReceiptStatement(payment, german) : "";

    private static string ReceiptStatement(PaymentEvent payment, bool german)
    {
        var issuer = ReceiptIssuer(payment);
        return german
            ? $"inkl. anfallender Steuern laut Beleg von {issuer}"
            : $"incl. applicable taxes as shown on the {issuer} receipt";
    }

    /// <summary>Managed Payments receipts read "Sold through Link" (the seller the intro names)</summary>
    private static string ReceiptIssuer(PaymentEvent payment) =>
        IsManagedPayments(payment) ? "Link" : PaymentDisplayNames.Provider(payment.PaymentProvider);

    private static bool IsLemonSqueezy(PaymentEvent payment) =>
        string.Equals(payment.PaymentProvider, LemonSqueezy, StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedPayments(PaymentEvent payment) =>
        string.Equals(payment.PaymentMethod, PurchaseConfirmationEmailService.ManagedPaymentsMethod, StringComparison.OrdinalIgnoreCase);

    private static string WithStatement(string amount, string statement) =>
        statement.Length == 0 ? amount : $"{amount} ({statement})";

    private static string Rate(PaymentEvent payment) =>
        (payment.VatRateBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Money(decimal amount, string currency) =>
        $"{amount.ToString("0.00##", CultureInfo.InvariantCulture)} {currency}".Trim();
}
