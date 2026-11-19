using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Coflnet.Sky.EventBroker.Models;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class PurchaseConfirmationEmailServiceTests
{
    [Test]
    public async Task SendWithTimeout_FailsWhenServerNeverAnswers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(); // accepts the connection (backlog) and never answers
        var service = new PurchaseConfirmationEmailService(
            new ConfigurationBuilder().Build(),
            NullLogger<PurchaseConfirmationEmailService>.Instance,
            null)
        { SendTimeout = TimeSpan.FromSeconds(1) };
        using var message = new MailMessage("a@example.com", "b@example.com", "s", "b");

        var watch = Stopwatch.StartNew();
        // the outer WaitAsync fails the test instead of hanging CI if the timeout ever stops reaching the send
        Assert.ThrowsAsync<TimeoutException>(() => service.SendWithTimeoutAsync(
            "127.0.0.1", port, new NetworkCredential("u", "p"), message, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

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

        Assert.That(text, Does.Contain("ORDER OVERVIEW\n--------------")
            .And.Contain("Product: premium")
            .And.Contain("Amount paid: 12.34 EUR")
            .And.Contain("Date: 2026-08-09 10:30:00 UTC")
            .And.Contain("Paid with: Stripe (Card)")
            .And.Contain("Transaction reference: ref-123")
            .And.Contain("WITHDRAWAL\n----------")
            .And.Contain(documents.Withdrawal.Url));
        foreach (var document in documents.AgreementDocuments)
            Assert.That(text, Does.Contain($"- {document.Title} ({document.Version})"));
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

    private static PaymentEvent PaidOrder(string provider, string method, string title = null) =>
        new()
        {
            ProductId = "144",
            ProductTitle = title,
            PaymentProvider = provider,
            PaymentMethod = method,
            PaymentProviderTransactionId = "4M9650370X895012P",
            PayedAmount = 1.15,
            Currency = "EUR",
            Timestamp = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc)
        };

    [Test]
    public void SenderAddress_HasDisplayNameByDefault()
    {
        var address = PurchaseConfirmationEmailService.SenderAddress(new ConfigurationBuilder().Build());

        Assert.That(address.ToString(), Is.EqualTo("\"Coflnet\" <noreply@coflnet.com>"));
    }

    [Test]
    public void SenderAddress_UsesConfiguredNameAndAddress()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["SMTP_FROM"] = "billing@example.com",
            ["SMTP_FROM_NAME"] = "Example Billing"
        }).Build();

        var address = PurchaseConfirmationEmailService.SenderAddress(config);

        Assert.That((address.DisplayName, address.Address), Is.EqualTo(("Example Billing", "billing@example.com")));
    }

    [Test]
    public void ReplyToAddress_DefaultsToSupportAndIsConfigurable()
    {
        var custom = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["SMTP_REPLY_TO"] = "help@example.com"
        }).Build();

        Assert.That(PurchaseConfirmationEmailService.ReplyToAddress(new ConfigurationBuilder().Build()).Address,
            Is.EqualTo("support@coflnet.com"));
        Assert.That(PurchaseConfirmationEmailService.ReplyToAddress(custom).Address, Is.EqualTo("help@example.com"));
    }

    [Test]
    public void BuildContent_ShowsProductTitleInsteadOfTheId()
    {
        var text = Build(PaidOrder("paypal", "paypal", "Premium+ (4 weeks)"), "de");

        Assert.That(text, Does.Contain("Produkt: Premium+ (4 weeks)").And.Not.Contain("144"));
    }

    [Test]
    public void BuildContent_FallsBackToProductIdWithoutTitle()
    {
        var text = Build(PaidOrder("paypal", "paypal"), "en");

        Assert.That(text, Does.Contain("Product: 144"));
    }

    [Test]
    public void BuildContent_UsesReadableProviderAndOmitsMethodRepeatingIt()
    {
        var text = Build(PaidOrder("paypal", "paypal", "Premium"), "de");

        Assert.That(text, Does.Contain("Zahlung über: PayPal\n").And.Not.Contain("PayPal (PayPal)"));
    }

    [TestCase("stripe", "card", "de", "Zahlung über: Stripe (Karte)")]
    [TestCase("stripe", "card", "en", "Paid with: Stripe (Card)")]
    [TestCase("stripe", "sepa_debit", "en", "Paid with: Stripe (SEPA direct debit)")]
    public void BuildContent_ShowsReadableMethod(string provider, string method, string locale, string expected)
    {
        var text = Build(PaidOrder(provider, method, "Premium"), locale);

        Assert.That(text, Does.Contain(expected));
    }

    [TestCase("de", "BESTELLÜBERSICHT", "WIDERRUF", "ANHÄNGE", "DATENSCHUTZ UND KONTAKT")]
    [TestCase("en", "ORDER OVERVIEW", "WITHDRAWAL", "ATTACHMENTS", "PRIVACY AND SUPPORT")]
    public void BuildContent_KeepsHumanSectionsInOrderEndingWithTheFooter(string locale, params string[] headings)
    {
        var text = Build(SubscriptionPayment("stripe", 4), locale);

        var positions = headings.Select(heading => text.IndexOf(heading + "\n", StringComparison.Ordinal)).ToArray();
        Assert.That(positions, Is.Ordered.And.All.GreaterThanOrEqualTo(0));
        Assert.That(text.IndexOf("Coflnet GmbH, Dorfstraße", StringComparison.Ordinal), Is.GreaterThan(positions[^1]));
        Assert.That(text, Does.EndWith("support@coflnet.com"));
    }

    [TestCase("de")]
    [TestCase("en")]
    public void Email_ContainsNoHashesDescriptorOrDocumentUrls(string locale)
    {
        var documents = TestDocuments(locale);
        var payment = SubscriptionPayment("stripe", 4);

        foreach (var body in new[]
        {
            PurchaseConfirmationEmailService.BuildContent(payment, locale, documents),
            PurchaseConfirmationEmailService.BuildHtml(payment, locale, documents)
        })
        {
            Assert.That(body, Does.Not.Contain(documents.AgreementHash)
                .And.Not.Contain(documents.AgreementUrl)
                .And.Not.Contain(documents.Withdrawal.Hash)
                .And.Not.Contain("SHA-256"));
            foreach (var document in documents.AgreementDocuments)
                Assert.That(body, Does.Not.Contain(document.Hash).And.Not.Contain(document.Url));
        }
    }

    [Test]
    public void CreateMessage_IsMultipartAlternativeWithoutAgreementJson()
    {
        var documents = TestDocuments("de");
        var payment = SubscriptionPayment("stripe", 4);

        using var message = PurchaseConfirmationEmailService.CreateMessage(
            new ConfigurationBuilder().Build(), "buyer@example.com", "de", payment, documents, "ref1");

        Assert.That(message.From.ToString(), Is.EqualTo("\"Coflnet\" <noreply@coflnet.com>"));
        Assert.That(message.ReplyToList.Single().Address, Is.EqualTo("support@coflnet.com"));
        Assert.That(message.IsBodyHtml, Is.False);
        Assert.That(message.Body, Does.Contain("BESTELLÜBERSICHT"));
        var html = message.AlternateViews.Single();
        Assert.That(html.ContentType.MediaType, Is.EqualTo("text/html"));
        Assert.That(message.Attachments.Select(a => a.Name), Is.EquivalentTo(
            documents.AgreementDocuments.Select(d => d.FileName).Append(documents.Withdrawal.FileName)));
        Assert.That(message.Attachments.Select(a => a.ContentType.MediaType), Does.Not.Contain("application/json"));
    }

    [Test]
    public void CreateMessage_AttachesOrderDetailsWhenPresent()
    {
        var payment = SubscriptionPayment("stripe", 4);
        payment.OrderDetailsJson = "{\"a\":1}";

        using var message = PurchaseConfirmationEmailService.CreateMessage(
            new ConfigurationBuilder().Build(), "buyer@example.com", "en", payment, TestDocuments("en"));

        Assert.That(message.Attachments.Select(a => a.Name), Does.Contain("order-details.json"));
    }

    [Test]
    public void Html_ContainsOverviewWithRenewalAndCancelLinkBeforeTheLegalParts()
    {
        var payment = SubscriptionPayment("stripe", 4);
        payment.ProductTitle = "Premium";
        payment.PayedAmount = 9.69;
        payment.SubscriptionRenewalAmount = 9.69m;

        var html = PurchaseConfirmationEmailService.BuildHtml(payment, "de", TestDocuments("de"));

        var overview = html.IndexOf("Bestellübersicht", StringComparison.Ordinal);
        var renewal = html.IndexOf("verlängert sich alle 4 Wochen zu 9.69 EUR bis zur Kündigung", StringComparison.Ordinal);
        var cancel = html.IndexOf("href=\"https://coflnet.com/de/cancel-contract\"", StringComparison.Ordinal);
        var withdrawal = html.IndexOf(">Widerruf<", StringComparison.Ordinal);
        Assert.That(new[] { overview, renewal, cancel, withdrawal }, Is.Ordered.And.All.GreaterThan(0));
        Assert.That(html, Does.Contain("Premium").And.Contain("9.69 EUR").And.Contain("<html lang=\"de\">")
            .And.Not.Contain("<img").And.Not.Contain("<script").And.Not.Contain("<link"));
    }

    [Test]
    public void Html_EscapesPaymentFields()
    {
        var payment = PaidOrder("paypal", "paypal", "<b>Evil</b> & Co");

        var html = PurchaseConfirmationEmailService.BuildHtml(payment, "en", TestDocuments("en"));

        Assert.That(html, Does.Contain("&lt;b&gt;Evil&lt;/b&gt; &amp; Co").And.Not.Contain("<b>Evil"));
    }

    [TestCase("en")]
    [TestCase("de")]
    public void TextAndHtml_ShowTheSameOverviewValues(string locale)
    {
        var payment = SubscriptionPayment("stripe", 4);
        payment.ProductTitle = "Premium";
        payment.PayedAmount = 9.69;
        var documents = TestDocuments(locale);

        var text = PurchaseConfirmationEmailService.BuildContent(payment, locale, documents);
        var html = PurchaseConfirmationEmailService.BuildHtml(payment, locale, documents);

        foreach (var value in new[] { "Premium", "9.69 EUR", "2026-11-18 10:00:00 UTC", "Stripe (", "sub-1" })
            Assert.That((text.Contains(value), html.Contains(value)), Is.EqualTo((true, true)), value);
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

        Assert.That(Build(payment, "en"), Does.Not.Contain("Billing period").And.Not.Contain("Cancel anytime"));
    }

    [Test]
    public void BuildContent_FourWeekPlanRenewsEveryFourWeeks()
    {
        var payment = SubscriptionPayment("stripe", 4);
        payment.SubscriptionRenewalAmount = 4.99m;

        Assert.That(Build(payment, "en"), Does.Contain("Billing period: 4 weeks")
            .And.Contain("renews every 4 weeks at 4.99 EUR until cancelled")
            .And.Contain("https://sky.coflnet.com/account")
            .And.Contain("https://coflnet.com/cancel-contract")
            .And.Contain("Purchase confirmation"));
        Assert.That(Build(payment, "de"), Does.Contain("Abrechnungszeitraum: 4 Wochen")
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

        Assert.That(Build(payment, "en"), Does.Contain("4-week price of 5.00 EUR until cancelled"));
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
        Assert.That(text, Does.Contain("Renewal and billing follow the checkout terms of Lemon Squeezy")
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
