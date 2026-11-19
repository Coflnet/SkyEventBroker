using System;
using System.Collections.Generic;
using System.Globalization;
using Coflnet.Sky.EventBroker.Models;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Builds the content of the email that announces the end of a 13 or 52 week subscription term:
/// what happens next, what it costs, and the optional offer to extend the term.
/// </summary>
internal static class RenewalReminderModelBuilder
{
    private const string ExtendUrl = "https://sky.coflnet.com/account?extend=";
    private const string CancelUrlEn = "https://coflnet.com/cancel-contract";
    private const string CancelUrlDe = "https://coflnet.com/de/cancel-contract";
    private const int DefaultRenewalWeeks = 4;

    public static string Subject(PaymentEvent payment, bool german)
    {
        RequireTerm(payment);
        var weeks = payment.SubscriptionBillingWeeks.Value;
        var product = PaymentDisplayNames.Product(payment).ReplaceLineEndings(" ");
        var date = Date(payment.SubscriptionTermEndsAt);
        return german
            ? $"Ihre {weeks}-Wochen-Laufzeit von {product} endet am {date}"
            : $"Your {product} {weeks}-week term ends on {date}";
    }

    /// <summary>A reminder without its term cannot state what ends when; throwing parks the delivery instead of sending a wrong email.</summary>
    private static void RequireTerm(PaymentEvent payment)
    {
        if (payment.SubscriptionTermEndsAt == null || payment.SubscriptionBillingWeeks is not > 0)
            throw new InvalidOperationException(
                "Renewal reminder lacks SubscriptionTermEndsAt or SubscriptionBillingWeeks");
    }

    public static PurchaseConfirmationModel Build(PaymentEvent payment, bool german)
    {
        RequireTerm(payment);
        var sections = new List<EmailSection>();
        if (payment.SubscriptionExtensionAmount is > 0 && !string.IsNullOrWhiteSpace(payment.SubscriptionExternalId))
            sections.Add(ExtensionOffer(payment, german));
        sections.Add(NextSteps(payment, german));
        return new PurchaseConfirmationModel(
            Subject(payment, german),
            german
                ? "Die aktuelle Laufzeit Ihres Abonnements endet bald. Das passiert danach:"
                : "The current term of your subscription ends soon. Here is what happens next.",
            german ? "Übersicht" : "Overview",
            OverviewRows(payment, german),
            [],
            sections,
            PurchaseConfirmationModelBuilder.Footer);
    }

    private static List<(string Label, string Value)> OverviewRows(PaymentEvent payment, bool german) =>
    [
        (german ? "Produkt" : "Product", PaymentDisplayNames.Product(payment)),
        (german ? "Aktuelle Laufzeit endet am" : "Current term ends", DateTimeUtc(payment.SubscriptionTermEndsAt)),
        (german ? "Danach" : "Afterwards", Afterwards(payment, german))
    ];

    private static string Afterwards(PaymentEvent payment, bool german)
    {
        var weeks = RenewalWeeks(payment);
        var amount = TaxStatement.RenewalAmount(payment, german);
        return german
            ? $"läuft alle {SubscriptionTermsSection.Weeks(weeks, true)}{SubscriptionTermsSection.Price(amount, " zu ")} weiter, jederzeit zum Ende des laufenden {weeks}-Wochen-Zeitraums kündbar"
            : $"continues every {SubscriptionTermsSection.Weeks(weeks, false)}{SubscriptionTermsSection.Price(amount, " at ")}, cancellable at any time, effective at the end of the current {weeks}-week period";
    }

    private static int RenewalWeeks(PaymentEvent payment) =>
        payment.SubscriptionRenewalWeeks is > 0 ? payment.SubscriptionRenewalWeeks.Value : DefaultRenewalWeeks;

    private static EmailSection ExtensionOffer(PaymentEvent payment, bool german)
    {
        var weeks = payment.SubscriptionBillingWeeks.Value;
        var amount = TaxStatement.ExtensionAmount(payment, german);
        var date = DateTimeUtc(payment.SubscriptionTermEndsAt);
        var link = ExtendUrl + Uri.EscapeDataString(payment.SubscriptionExternalId ?? "");
        return new EmailSection(
            german ? $"Lieber weitere {weeks} Wochen?" : $"Prefer another {weeks} weeks?",
            [
                german
                    ? $"Verlängern Sie stattdessen um weitere {SubscriptionTermsSection.Weeks(weeks, true)} zum Preis von {amount}."
                    : $"Renew for another {SubscriptionTermsSection.Weeks(weeks, false)} for {amount} instead.",
                german ? $"Verlängern: {link}" : $"Renew: {link}",
                german ? $"Das muss vor dem {date} geschehen." : $"This must be done before {date}."
            ]);
    }

    private static EmailSection NextSteps(PaymentEvent payment, bool german) =>
        new(
            german ? "Was Sie tun müssen" : "What you need to do",
            [
                german
                    ? $"Nichts, wenn Sie mit der Abrechnung alle {RenewalWeeks(payment)} Wochen einverstanden sind."
                    : $"Nothing, if you are fine with billing every {RenewalWeeks(payment)} weeks.",
                german
                    ? $"Zum Kündigen: in Ihrem Konto unter {SubscriptionTermsSection.AccountUrl} (Abonnement kündigen) oder über die Kündigungsseite {CancelUrlDe}"
                    : $"To cancel: in your account at {SubscriptionTermsSection.AccountUrl} (Cancel subscription) or via the cancellation page {CancelUrlEn}"
            ]);

    private static string DateTimeUtc(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "";

    private static string Date(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
}
