using System;
using System.Text;
using Coflnet.Sky.EventBroker.Models;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// PAngV / Art. 246a EGBGB: the confirmation states the total price including taxes, and only what is true for the sale.
/// </summary>
public class TaxStatementTests
{
    [Test]
    public void DirectGermanSaleStatesVatRateAndAmount()
    {
        var payment = Subscription("stripe", "card", 11.53, TaxStatement.Included, 1900, 1.84m);

        AssertInTextAndHtml(payment, "de",
            "Bezahlter Betrag: 11.53 EUR (inkl. 19 % MwSt.: 1.84 EUR)",
            "verlängert sich alle 4 Wochen zu 11.53 EUR (inkl. 19 % MwSt.) bis zur Kündigung");
        AssertInTextAndHtml(payment, "en",
            "Amount paid: 11.53 EUR (incl. 19% VAT: 1.84 EUR)",
            "renews every 4 weeks at 11.53 EUR (incl. 19% VAT) until cancelled");
    }

    [Test]
    public void LongPlanStatesVatOfTheRenewalPrice()
    {
        var payment = Subscription("paypal", "paypal", 118.63, TaxStatement.Included, 1900, 18.94m);
        payment.SubscriptionBillingWeeks = 52;
        payment.SubscriptionRenewalWeeks = 4;

        AssertInTextAndHtml(payment, "de",
            "zum 4-Wochen-Preis von 11.53 EUR (inkl. 19 % MwSt.) weiter bis zur Kündigung");
        AssertInTextAndHtml(payment, "en",
            "at the 4-week price of 11.53 EUR (incl. 19% VAT) until cancelled");
    }

    [Test]
    public void IncludedVatWithoutRateSaysOnlyThatItIsIncluded()
    {
        var payment = Subscription("stripe", "card", 11.53, TaxStatement.Included, 0, null);

        AssertInTextAndHtml(payment, "de", "Bezahlter Betrag: 11.53 EUR (inkl. MwSt.)");
        AssertInTextAndHtml(payment, "en", "Amount paid: 11.53 EUR (incl. VAT)");
    }

    [Test]
    public void UsSaleWithoutTaxMakesNoTaxClaim()
    {
        var payment = Subscription("stripe", "card", 9.69, TaxStatement.None, 0, null);
        payment.SubscriptionRenewalAmount = 9.69m;

        AssertNoTaxClaim(payment, "Bezahlter Betrag: 9.69 EUR\n", "zu 9.69 EUR bis zur Kündigung");
    }

    /// <summary>
    /// Regression: a Managed Payments charge without reported tax (e.g. a US state without tax on digital goods) said
    /// "plus applicable taxes", although the paid amount is the whole charge. Only the net renewal price is "plus taxes".
    /// </summary>
    [Test]
    public void ManagedPaymentsChargeWithoutReportedTaxRefersToTheLinkReceipt()
    {
        var payment = Subscription("stripe", "stripe_managed_payments", 9.69, TaxStatement.Included, 0, null);
        payment.SubscriptionRenewalAmount = 9.69m;
        payment.RenewalTaxTreatment = TaxStatement.Net;

        AssertInTextAndHtml(payment, "de",
            "Bezahlter Betrag: 9.69 EUR (inkl. anfallender Steuern laut Beleg von Link)",
            "zu 9.69 EUR (zzgl. anfallender Steuern) bis zur Kündigung");
        AssertInTextAndHtml(payment, "en",
            "Amount paid: 9.69 EUR (incl. applicable taxes as shown on the Link receipt)",
            "at 9.69 EUR (plus applicable taxes) until cancelled");
    }

    [Test]
    public void ManagedPaymentsInclusiveRenewalPriceIncludesTaxes()
    {
        var payment = Subscription("stripe", "stripe_managed_payments", 9.69, TaxStatement.Included, 0, null);
        payment.SubscriptionRenewalAmount = 9.69m;

        AssertInTextAndHtml(payment, "de", "zu 9.69 EUR (inkl. Steuern) bis zur Kündigung");
        AssertInTextAndHtml(payment, "en", "at 9.69 EUR (incl. taxes) until cancelled");
    }

    /// <summary>Older Managed Payments events: amount_paid is the whole charge, so the receipt wording is true as well</summary>
    [Test]
    public void OldManagedPaymentsEventRefersToTheLinkReceipt()
    {
        var payment = Subscription("stripe", "stripe_managed_payments", 9.69, null, 0, null);

        AssertInTextAndHtml(payment, "de", "Bezahlter Betrag: 9.69 EUR (inkl. anfallender Steuern laut Beleg von Link)");
    }

    /// <summary>Stripe reported tax on the invoice: the paid amount contains it, the renewal price stays net</summary>
    [Test]
    public void ManagedPaymentsTaxReportedByStripeIsIncludedInThePaidAmount()
    {
        var payment = Subscription("stripe", "stripe_managed_payments", 8.72, TaxStatement.Included, 0, 1.45m);
        payment.SubscriptionRenewalAmount = 7.27m;
        payment.RenewalTaxTreatment = TaxStatement.Net;

        AssertInTextAndHtml(payment, "de",
            "Bezahlter Betrag: 8.72 EUR (inkl. Steuern: 1.45 EUR)",
            "zu 7.27 EUR (zzgl. anfallender Steuern) bis zur Kündigung");
        AssertInTextAndHtml(payment, "en", "Amount paid: 8.72 EUR (incl. taxes: 1.45 EUR)");
    }

    [Test]
    public void OldEventWithoutTaxFieldsMakesNoTaxClaim()
    {
        var payment = Subscription("stripe", "card", 11.86, null, 0, null);
        payment.SubscriptionRenewalAmount = 11.86m;

        AssertNoTaxClaim(payment, "Bezahlter Betrag: 11.86 EUR\n", "zu 11.86 EUR bis zur Kündigung");
    }

    /// <summary>Lemon Squeezy's order total, which the event carries, includes the taxes it charged as merchant of record</summary>
    [Test]
    public void LemonSqueezyRefersToItsReceipt()
    {
        var payment = Subscription("lemonsqueezy", "card", 11.86, null, 0, null);

        AssertInTextAndHtml(payment, "de", "Bezahlter Betrag: 11.86 EUR (inkl. anfallender Steuern laut Beleg von Lemon Squeezy)");
        AssertInTextAndHtml(payment, "en", "Amount paid: 11.86 EUR (incl. applicable taxes as shown on the Lemon Squeezy receipt)");
    }

    private static void AssertNoTaxClaim(PaymentEvent payment, string paidLine, string renewal)
    {
        var text = Build(payment, "de");
        Assert.That(text, Does.Contain(paidLine).And.Contain(renewal));
        Assert.That(text, Does.Not.Contain("MwSt").And.Not.Contain("inkl.").And.Not.Contain("Steuern"));
        Assert.That(Build(payment, "en"), Does.Not.Contain("VAT").And.Not.Contain("incl.").And.Not.Contain("tax"));
    }

    // The overview row renders as "label: value" in the text and as separate table cells in the HTML.
    private static void AssertInTextAndHtml(PaymentEvent payment, string locale, params string[] expected)
    {
        var text = Build(payment, locale);
        var html = PurchaseConfirmationEmailService.BuildHtml(payment, locale, Documents(locale));
        foreach (var value in expected)
        {
            Assert.That(text, Does.Contain(value), "text");
            var parts = value.Split(": ", 2);
            Assert.That(html, Does.Contain(parts.Length == 2 && IsOverviewLabel(parts[0]) ? parts[1] : value), "html");
        }
    }

    private static bool IsOverviewLabel(string label) => label is "Bezahlter Betrag" or "Amount paid";

    private static PaymentEvent Subscription(string provider, string method, double paid, string treatment, int basisPoints,
        decimal? tax) =>
        new()
        {
            ProductId = "premium",
            PaymentProvider = provider,
            PaymentMethod = method,
            PaymentProviderTransactionId = "sub-1",
            PayedAmount = paid,
            Currency = "EUR",
            Timestamp = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc),
            SubscriptionBillingWeeks = 4,
            SubscriptionRenewalAmount = 11.53m,
            TaxTreatment = treatment,
            TaxAmount = tax,
            VatRateBasisPoints = basisPoints,
            TaxCountry = basisPoints > 0 ? "DE" : null,
            RenewalTaxTreatment = treatment
        };

    private static string Build(PaymentEvent payment, string locale) =>
        PurchaseConfirmationEmailService.BuildContent(payment, locale, Documents(locale));

    private static LegalDocuments Documents(string language) =>
        new(
            "skycofl",
            new string('a', 64),
            $"https://coflnet.com/legal/agreements/{new string('a', 64)}.json",
            Encoding.UTF8.GetBytes("{}"),
            [Document("terms", language), Document("withdrawal", language)],
            Document("withdrawal", language));

    private static LegalDocument Document(string key, string language) =>
        new(key, key, $"{key}-{language}.md", "2026-08-08", $"https://coflnet.com/legal/archive/{key}-{language}.md",
            Encoding.UTF8.GetBytes(key), $"{key}-{language}-hash", $"{key}-acceptance-hash");
}
