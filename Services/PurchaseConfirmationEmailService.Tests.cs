using System;
using System.Linq;
using System.Text;
using Coflnet.Sky.EventBroker.Models;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class PurchaseConfirmationEmailServiceTests
{
    [Test]
    public void BuildContent_ContainsTransactionAgreementAndWithdrawalDetails()
    {
        var documents = TestDocuments("en");
        var text = PurchaseConfirmationEmailService.BuildContent(
            new PaymentEvent
            {
                ProductId = "premium",
                PayedAmount = 12.34,
                Currency = "EUR",
                PaymentMethod = "card",
                PaymentProvider = "stripe",
                PaymentProviderTransactionId = "ref-123",
                Timestamp = new DateTime(2026, 8, 9, 10, 30, 0, DateTimeKind.Utc)
            },
            "en",
            documents);

        Assert.That(text, Does.Contain("ORDER\n-----")
            .And.Contain("PAYMENT\n-------")
            .And.Contain("AGREEMENT\n---------")
            .And.Contain("WITHDRAWAL\n----------")
            .And.Contain("Amount: 12.34 EUR")
            .And.Contain("Payment method: card")
            .And.Contain(documents.AgreementHash)
            .And.Contain(documents.AgreementUrl)
            .And.Contain(documents.Withdrawal.Hash));
        foreach (var document in documents.AgreementDocuments)
            Assert.That(text, Does.Contain(document.Url).And.Contain(document.Hash));
    }

    [Test]
    public void BuildContent_ServicePurchaseSeparatesPeriodAndDeclaration()
    {
        var text = PurchaseConfirmationEmailService.BuildContent(
            new PaymentEvent
            {
                ProductId = "premium_plus-day",
                PaymentProvider = "coflcoins",
                PaymentProviderTransactionId = "367796",
                ConfirmationType = "service_purchase",
                CoinAmount = 600,
                ServiceStartsAtUtc = new DateTime(2026, 8, 12, 11, 33, 53,
                    DateTimeKind.Utc),
                ServiceEndsAtUtc = new DateTime(2026, 8, 13, 11, 33, 53,
                    DateTimeKind.Utc),
                DeclarationVersion = "premium-service-start-2026-07-28",
                DeclarationText = "I request early performance.",
                Timestamp = new DateTime(2026, 8, 12, 11, 33, 54,
                    DateTimeKind.Utc)
            },
            "en",
            TestDocuments("en"));

        Assert.That(text, Does.Contain("CoflCoins: 600")
            .And.Contain("SERVICE PERIOD\n--------------")
            .And.Contain("EARLY-PERFORMANCE DECLARATION\n-----------------------------")
            .And.Contain("Version: premium-service-start-2026-07-28")
            .And.Contain("I request early performance."));
    }

    [Test]
    public void BuildContent_ExpertConfigContainsOrderQuoteBeforeSupply()
    {
        var documents = TestDocuments("en") with
        {
            AgreementId = "expertMarketplace"
        };
        var text = PurchaseConfirmationEmailService.BuildContent(
            new PaymentEvent
            {
                ProductId = "config-purchase",
                PaymentProvider = "coflcoins",
                PaymentProviderTransactionId = "42",
                ConfirmationType = "service_purchase",
                CoinAmount = 600,
                AgreementId = "expertMarketplace",
                GrossEurCents = 223,
                VatEurCents = 36,
                TaxCountry = "DE",
                ConsumerRightsRegime = "EU",
                VatRateBasisPoints = 1900,
                OrderDetailsJson = "{\"config\":{\"name\":\"Expert One\",\"version\":3}}",
                Timestamp = DateTime.UtcNow
            },
            "en",
            documents);

        Assert.That(text, Does.Contain("licence to the identified Expert Config version")
            .And.Contain("managed update facility are supplied immediately")
            .And.Contain("Recorded tax-inclusive value: EUR 2.23")
            .And.Contain("including EUR 0.36 VAT; DE, 19%; EU consumer-rights regime")
            .And.Contain("ORDER DETAILS")
            .And.Contain("Expert One")
            .And.Not.Contain("SERVICE PERIOD"));
    }

    [Test]
    public void BuildContent_MerchantOfRecordIsOnlyFulfillmentNotice()
    {
        var text = PurchaseConfirmationEmailService.BuildContent(
            new PaymentEvent
            {
                ProductId = "cc_1800",
                PaymentProvider = "lemonsqueezy",
                PaymentProviderTransactionId = "order-123",
                Timestamp = DateTime.UtcNow
            },
            "en",
            TestDocuments("en"));

        Assert.That(text, Does.Contain("Fulfillment confirmation")
            .And.Contain("merchant of record")
            .And.Contain("authoritative order, payment and withdrawal information")
            .And.Contain("separate SkyCofl usage relationship")
            .And.Not.Contain("contract with Coflnet GmbH"));
    }

    [Test]
    public void BuildContent_UsesGermanDocumentsForGermanLocale()
    {
        var documents = TestDocuments("de");
        var text = PurchaseConfirmationEmailService.BuildContent(
            new PaymentEvent
            {
                ProductId = "premium",
                PaymentProvider = "stripe",
                PaymentProviderTransactionId = "ref-123",
                Timestamp = DateTime.UtcNow
            },
            "de-DE",
            documents);

        Assert.That(text, Does.Contain("Kaufbestätigung")
            .And.Contain("Widerrufsbelehrung")
            .And.Not.Contain("Purchase confirmation"));
        foreach (var document in documents.AgreementDocuments)
            Assert.That(document.FileName, Does.Contain("-de-"));
    }

    [Test]
    public void PurchaseConfirmationReference_IsStableAndSeparatesType()
    {
        var reference = MessageService.PurchaseConfirmationReference(
            "provider",
            "order-123");

        Assert.That(reference, Has.Length.LessThanOrEqualTo(32));
        Assert.That(reference, Is.EqualTo(
            MessageService.PurchaseConfirmationReference(" Provider ", " order-123 ")));
        Assert.That(reference, Is.Not.EqualTo(
            MessageService.PurchaseConfirmationReference(
                "provider",
                "order-123",
                "trial")));
    }

    [Test]
    public void ValidateAgreement_MatchingTermsAcceptanceHashDoesNotThrow()
    {
        var documents = TestDocuments("en");
        var terms = documents.AgreementDocuments.Single(
            document => document.Key == "terms");

        Assert.DoesNotThrow(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent { TermsAcceptanceHash = terms.AcceptanceHash },
            documents));
    }

    [Test]
    public void ValidateAgreement_MatchingRootAndWithdrawalHashDoesNotThrow()
    {
        var documents = TestDocuments("en");

        Assert.DoesNotThrow(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent
            {
                AgreementId = documents.AgreementId,
                AgreementHash = documents.AgreementHash,
                WithdrawalSha256 = documents.Withdrawal.Hash
            },
            documents));
    }

    [TestCase("other", null)]
    [TestCase(null, "wrong-hash")]
    public void ValidateAgreement_MismatchedRootThrows(
        string agreementId,
        string agreementHash)
    {
        var documents = TestDocuments("en");

        Assert.Throws<InvalidOperationException>(() =>
            PurchaseConfirmationEmailService.ValidateAgreement(
                new PaymentEvent
                {
                    AgreementId = agreementId ?? documents.AgreementId,
                    AgreementHash = agreementHash ?? documents.AgreementHash
                },
                documents));
    }

    [Test]
    public void ValidateAgreement_MismatchedTermsAcceptanceHashThrows()
    {
        var documents = TestDocuments("en");

        Assert.Throws<InvalidOperationException>(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent { TermsAcceptanceHash = "not-the-recorded-hash" },
            documents));
    }

    [Test]
    public void ValidateAgreement_MatchingWithdrawalVersionDoesNotThrow()
    {
        var documents = TestDocuments("en");

        Assert.DoesNotThrow(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent { WithdrawalVersion = documents.Withdrawal.Version },
            documents));
    }

    [Test]
    public void ValidateAgreement_MismatchedWithdrawalVersionThrows()
    {
        var documents = TestDocuments("en");

        Assert.Throws<InvalidOperationException>(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent { WithdrawalVersion = "not-the-loaded-version" },
            documents));
    }

    [Test]
    public void ValidateAgreement_BlankFieldsAreNotValidated()
    {
        var documents = TestDocuments("en");

        Assert.DoesNotThrow(() => PurchaseConfirmationEmailService.ValidateAgreement(
            new PaymentEvent(),
            documents));
    }

    private static PaymentEvent SubscriptionPayment(
        string provider, int weeks, string method = "card") =>
        new()
        {
            ProductId = "premium",
            PaymentProvider = provider,
            PaymentMethod = method,
            PaymentProviderTransactionId = "sub-1",
            Currency = "EUR",
            SubscriptionBillingWeeks = weeks,
            Timestamp = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc)
        };

    private static string Build(PaymentEvent payment, string locale) =>
        PurchaseConfirmationEmailService.BuildContent(payment, locale, TestDocuments(locale));

    [Test]
    public void BuildContent_NoSubscriptionSectionWithoutBillingWeeks()
    {
        var payment = SubscriptionPayment("stripe", 4);
        payment.SubscriptionBillingWeeks = null;

        Assert.That(Build(payment, "en"), Does.Not.Contain("SUBSCRIPTION\n"));
    }

    [Test]
    public void BuildContent_FourWeekPlanRenewsEveryFourWeeks()
    {
        var payment = SubscriptionPayment("stripe", 4);
        payment.SubscriptionRenewalAmount = 4.99m;

        Assert.That(Build(payment, "en"), Does.Contain("SUBSCRIPTION\n------------")
            .And.Contain("Billing period: 4 weeks")
            .And.Contain("renews every 4 weeks at 4.99 EUR until cancelled")
            .And.Contain("https://sky.coflnet.com/account")
            .And.Contain("https://coflnet.com/cancel-contract")
            .And.Contain("Purchase confirmation"));
        Assert.That(Build(payment, "de"), Does.Contain("ABONNEMENT\n----------")
            .And.Contain("Abrechnungszeitraum: 4 Wochen")
            .And.Contain("verlängert sich alle 4 Wochen zu 4.99 EUR bis zur Kündigung")
            .And.Contain("https://coflnet.com/de/cancel-contract"));
    }

    [Test]
    public void BuildContent_LongPlanFirstTermContinuesEveryFourWeeksWithoutAmountWhenUnknown()
    {
        var payment = SubscriptionPayment("paypal", 52);

        var en = Build(payment, "en");
        Assert.That(en, Does.Contain("Billing period: 52 weeks")
            .And.Contain("After the first 52 weeks it continues every 4 weeks at the 4-week price until cancelled"));
        Assert.That(Build(payment, "de"), Does.Contain("Nach den ersten 52 Wochen läuft das Abonnement alle 4 Wochen zum 4-Wochen-Preis weiter bis zur Kündigung"));
    }

    [Test]
    public void BuildContent_LongPlanFirstTermShowsRenewalPriceWhenProvided()
    {
        var payment = SubscriptionPayment("stripe", 13);
        payment.SubscriptionRenewalAmount = 5m;

        Assert.That(Build(payment, "en"), Does.Contain("4-week price (5.00 EUR) until cancelled"));
    }

    [Test]
    public void BuildContent_PlanRepeatingItsTermIsNotClaimedToContinueEveryFourWeeks()
    {
        var payment = SubscriptionPayment("paypal", 13);
        payment.SubscriptionRenewalWeeks = 13;

        Assert.That(Build(payment, "en"), Does.Contain("renews every 13 weeks until cancelled")
            .And.Not.Contain("4-week price"));
    }

    [Test]
    public void BuildContent_LemonSqueezyKeepsItsOwnRenewalWording()
    {
        var payment = SubscriptionPayment("lemonsqueezy", 52);

        var text = Build(payment, "en");
        Assert.That(text, Does.Contain("Renewal and billing follow the checkout terms of lemonsqueezy")
            .And.Contain("Cancel anytime")
            .And.Contain("Fulfillment confirmation")
            .And.Not.Contain("every 4 weeks")
            .And.Not.Contain("4-week price"));
    }

    [Test]
    public void BuildContent_ManagedPaymentsSubscriptionIsMerchantOfRecordSaleWithContinuation()
    {
        var payment = SubscriptionPayment("stripe", 52, "stripe_managed_payments");

        var en = Build(payment, "en");
        Assert.That(en, Does.StartWith("Fulfillment confirmation")
            .And.Contain("Sold through Link, LLC (Stripe Managed Payments) was the seller (merchant of record)")
            .And.Contain("continues every 4 weeks at the 4-week price"));
        Assert.That(Build(payment, "de"), Does.StartWith("Bereitstellungsbestätigung")
            .And.Not.StartWith("Kaufbestätigung"));
    }

    [Test]
    public void BuildContent_DirectStripeSubscriptionStaysCoflnetSale()
    {
        var text = Build(SubscriptionPayment("stripe", 4), "en");

        Assert.That(text, Does.StartWith("Purchase confirmation")
            .And.Not.Contain("merchant of record"));
    }

    [Test]
    public void IsMerchantOfRecord_ClassifiesProvidersAndManagedPayments()
    {
        Assert.That(PurchaseConfirmationEmailService.IsMerchantOfRecord(
            new PaymentEvent { PaymentProvider = "stripe", PaymentMethod = "stripe_managed_payments" }), Is.True);
        Assert.That(PurchaseConfirmationEmailService.IsMerchantOfRecord(
            new PaymentEvent { PaymentProvider = "lemonsqueezy" }), Is.True);
        Assert.That(PurchaseConfirmationEmailService.IsMerchantOfRecord(
            new PaymentEvent { PaymentProvider = "Google Play" }), Is.True);
        Assert.That(PurchaseConfirmationEmailService.IsMerchantOfRecord(
            new PaymentEvent { PaymentProvider = "stripe", PaymentMethod = "card" }), Is.False);
        Assert.That(PurchaseConfirmationEmailService.IsMerchantOfRecord(
            new PaymentEvent { PaymentProvider = "paypal", PaymentMethod = "paypal" }), Is.False);
    }

    [Test]
    public void BuildContent_RenewalIsLabelledAsRenewalConfirmation()
    {
        var direct = SubscriptionPayment("stripe", 4);
        direct.SubscriptionIsRenewal = true;
        var managed = SubscriptionPayment("stripe", 4, "stripe_managed_payments");
        managed.SubscriptionIsRenewal = true;

        Assert.That(Build(direct, "en"), Does.StartWith("Renewal confirmation"));
        Assert.That(Build(direct, "de"), Does.StartWith("Verlängerungsbestätigung"));
        Assert.That(Build(managed, "en"), Does.StartWith("Fulfillment confirmation — renewal"));
    }

    private static LegalDocuments TestDocuments(string language) =>
        new(
            "skycofl",
            new string('a', 64),
            $"https://coflnet.com/legal/agreements/{new string('a', 64)}.json",
            Encoding.UTF8.GetBytes("{}"),
            [
                Document("terms", language),
                Document("commerceTerms", language),
                Document("aiTerms", language),
                Document("skycoflTerms", language)
            ],
            Document("withdrawal", language));

    private static LegalDocument Document(string key, string language) =>
        new(
            key,
            key,
            $"{key}-{language}-2026-08-08.md",
            "2026-08-08",
            $"https://coflnet.com/legal/archive/{key}-{language}-2026-08-08.md",
            Encoding.UTF8.GetBytes(key),
            $"{key}-{language}-hash",
            $"{key}-acceptance-hash");
}
