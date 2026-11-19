using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Coflnet.Sky.EventBroker.Models;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class RenewalReminderModelBuilderTests
{
    private static PaymentEvent Reminder(string treatment = TaxStatement.Included, decimal? extension = 29.99m) =>
        new()
        {
            ConfirmationType = "subscription_renewal_reminder",
            PaymentProvider = "stripe",
            PaymentMethod = "card",
            PaymentProviderTransactionId = "renewal-reminder:sub_1:20261201",
            ProductId = "premium",
            ProductTitle = "Premium",
            Currency = "EUR",
            SubscriptionBillingWeeks = 13,
            SubscriptionRenewalWeeks = 4,
            SubscriptionRenewalAmount = 9.99m,
            RenewalTaxTreatment = treatment,
            SubscriptionTermEndsAt = new DateTime(2026, 12, 8, 12, 0, 0, DateTimeKind.Utc),
            SubscriptionExtensionAmount = extension,
            SubscriptionExternalId = "sub 1/x"
        };

    private static string Text(PaymentEvent payment, bool german) =>
        PurchaseConfirmationRenderer.ToText(RenewalReminderModelBuilder.Build(payment, german));

    private static string Html(PaymentEvent payment, bool german) =>
        PurchaseConfirmationRenderer.ToHtml(RenewalReminderModelBuilder.Build(payment, german), german ? "de" : "en");

    [Test]
    public void Subject_IsLocalised()
    {
        Assert.That(RenewalReminderModelBuilder.Subject(Reminder(), false), Is.EqualTo("Your Premium 13-week term ends on 2026-12-08"));
        Assert.That(RenewalReminderModelBuilder.Subject(Reminder(), true), Is.EqualTo("Ihre 13-Wochen-Laufzeit von Premium endet am 2026-12-08"));
    }

    [Test]
    public void English_WithExtensionOffer()
    {
        var payment = Reminder();
        foreach (var content in new[] { Text(payment, false), Html(payment, false) })
            Assert.That(content, Does.Contain("2026-12-08")
                .And.Contain("continues every 4 weeks at 9.99 EUR (incl. VAT)")
                .And.Contain("cancellable at any time, effective at the end of the current 4-week period")
                .And.Contain("Renew for another 13 weeks for 29.99 EUR (incl. VAT) instead")
                .And.Contain("https://sky.coflnet.com/account?extend=sub%201%2Fx")
                .And.Contain("This must be done before 2026-12-08 12:00 UTC")
                .And.Contain("https://coflnet.com/cancel-contract")
                .And.Contain("Coflnet GmbH"));
        Assert.That(Text(payment, false), Does.Contain("Nothing, if you are fine with billing every 4 weeks"));
    }

    [Test]
    public void German_WithExtensionOffer()
    {
        var text = Text(Reminder(), true);

        Assert.That(text, Does.Contain("läuft alle 4 Wochen zu 9.99 EUR (inkl. MwSt.) weiter")
            .And.Contain("um weitere 13 Wochen zum Preis von 29.99 EUR (inkl. MwSt.)")
            .And.Contain("Das muss vor dem 2026-12-08 12:00 UTC geschehen")
            .And.Contain("Nichts, wenn Sie"));
    }

    [Test]
    public void WithoutExtensionAmount_NoOfferIsMade()
    {
        var payment = Reminder(extension: null);

        foreach (var content in new[] { Text(payment, false), Html(payment, false) })
            Assert.That(content, Does.Not.Contain("extend=").And.Not.Contain("Prefer another"));
        Assert.That(Text(payment, true), Does.Not.Contain("Lieber weitere"));
    }

    [Test]
    public void TaxStatement_FollowsRenewalTaxTreatment()
    {
        Assert.That(Text(Reminder(TaxStatement.Net), false), Does.Contain("9.99 EUR (plus applicable taxes)").And.Contain("29.99 EUR (plus applicable taxes)"));
        Assert.That(Text(Reminder(TaxStatement.Net), true), Does.Contain("9.99 EUR (zzgl. anfallender Steuern)"));
        var none = Text(Reminder(TaxStatement.None), false);
        Assert.That(none, Does.Contain("at 9.99 EUR,").And.Not.Contain("VAT").And.Not.Contain("tax"));
    }

    [Test]
    public void Message_HasReminderSubjectNoLegalAttachmentsAndSupportReplyTo()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        using var message = PurchaseConfirmationEmailService.CreateReminderMessage(config, "buyer@example.test", "de-DE", Reminder(), "mailabc");

        Assert.That(message.Subject, Is.EqualTo("Ihre 13-Wochen-Laufzeit von Premium endet am 2026-12-08"));
        Assert.That(message.Attachments, Is.Empty);
        Assert.That(message.AlternateViews, Has.Count.EqualTo(1));
        Assert.That(message.ReplyToList[0].Address, Is.EqualTo("support@coflnet.com"));
        Assert.That(message.Body, Does.Contain("Coflnet GmbH"));
    }

    [Test]
    public void ExtensionIsOnlyOfferedWithAmountAndExternalId()
    {
        var payment = Reminder();
        payment.SubscriptionExternalId = " ";

        Assert.That(Text(payment, false), Does.Not.Contain("Prefer another"));
    }

    [Test]
    public void ReminderWithoutTermIsRejected()
    {
        var noEnd = Reminder();
        noEnd.SubscriptionTermEndsAt = null;
        var noWeeks = Reminder();
        noWeeks.SubscriptionBillingWeeks = null;

        Assert.Throws<InvalidOperationException>(() => RenewalReminderModelBuilder.Build(noEnd, false));
        Assert.Throws<InvalidOperationException>(() => RenewalReminderModelBuilder.Subject(noWeeks, true));
    }

    [Test]
    public void SubjectHasNoLineBreaks()
    {
        var payment = Reminder();
        payment.ProductTitle = "Pre\r\nmium";

        Assert.That(RenewalReminderModelBuilder.Subject(payment, false), Does.Not.Contain("\n").And.Not.Contain("\r"));
    }

    [Test]
    public void RenewalWeeksComeFromTheEvent()
    {
        var payment = Reminder();
        payment.SubscriptionRenewalWeeks = 2;

        Assert.That(Text(payment, false), Does.Contain("billing every 2 weeks").And.Contain("current 2-week period"));
    }

    [Test]
    public void ConfirmationTypeIsMatchedTrimmed()
    {
        var payment = Reminder();
        payment.ConfirmationType = " Subscription_Renewal_Reminder ";

        Assert.That(PurchaseConfirmationEmailService.IsRenewalReminder(payment), Is.True);
    }

    [Test]
    public void SendAsync_TakesTheReminderPath()
    {
        // The legal document provider is null: a purchase confirmation would fail with a NullReferenceException loading
        // documents, a reminder goes straight to the (here unreachable) SMTP server and a rejected incomplete event never gets there.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["SMTP_HOST"] = "127.0.0.1", ["SMTP_PORT"] = "1", ["SMTP_USER"] = "u", ["SMTP_PASSWORD"] = "p"
        }).Build();
        var service = new PurchaseConfirmationEmailService(
            config, NullLogger<PurchaseConfirmationEmailService>.Instance, null) { SendTimeout = TimeSpan.FromSeconds(5) };
        var noTerm = Reminder();
        noTerm.SubscriptionTermEndsAt = null;

        Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("a@example.test", "en", noTerm, "ref"));
        var exception = Assert.CatchAsync(() => service.SendAsync("a@example.test", "en", Reminder(), "ref"));
        Assert.That(exception, Is.Not.InstanceOf<NullReferenceException>());
    }
}
