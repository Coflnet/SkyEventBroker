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
