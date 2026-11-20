using System;
using System.Collections.Generic;
using System.Globalization;
using Coflnet.Sky.EventBroker.Models;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// The subscription terms (period, renewal, how to cancel) of a subscription purchase confirmation.
/// </summary>
internal static class SubscriptionTermsSection
{
    public const string AccountUrl = "https://sky.coflnet.com/account";
    private const string BazaarProAccountUrl = "https://pro.skyblock.bz/dashboard/settings/billing";
    private const string CancelUrlEn = "https://coflnet.com/cancel-contract";
    private const string CancelUrlDe = "https://coflnet.com/de/cancel-contract";
    private const int DefaultRenewalWeeks = 4;

    /// <summary>Where the buyer manages the subscription: BazaarPro purchases have their own billing page.</summary>
    internal static string AccountUrlFor(PaymentEvent payment) =>
        PurchaseConfirmationModelBuilder.IsBazaarPro(payment) ? BazaarProAccountUrl : AccountUrl;

    public static bool IsSubscription(PaymentEvent payment) =>
        payment.SubscriptionBillingWeeks is > 0;

    internal const string TrialStartedType = "subscription_trial_started";

    /// <summary>The confirmation of a subscription that started with a free trial: no payment has been taken yet.</summary>
    public static bool IsTrialStart(PaymentEvent payment) =>
        IsSubscription(payment)
        && string.Equals(payment.ConfirmationType?.Trim(), TrialStartedType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first payment after a trial, when the event names the day the trial ended.</summary>
    private static bool IsFirstPaymentAfterTrial(PaymentEvent payment) =>
        payment.SubscriptionFollowsTrial == true && payment.SubscriptionTrialEndsAt.HasValue;

    /// <summary>When the trial ends and the first charge is due.</summary>
    internal static DateTime? TrialEnd(PaymentEvent payment) =>
        payment.SubscriptionTrialEndsAt ?? payment.SubscriptionNextBillingAt ?? payment.ServiceEndsAtUtc;

    /// <summary>When the trial ends, what is charged then and how it continues, e.g. for the intro of a trial start.</summary>
    internal static string TrialChargeSentence(PaymentEvent payment, bool german)
    {
        var end = FormatUtc(TrialEnd(payment));
        var first = TaxStatement.FirstChargeAmount(payment, german);
        var renewal = TaxStatement.RenewalAmount(payment, german);
        var weeks = payment.SubscriptionBillingWeeks.Value;
        var next = payment.SubscriptionRenewalWeeks is > 0 ? payment.SubscriptionRenewalWeeks.Value : DefaultRenewalWeeks;
        var when = end.Length > 0 ? end : german ? "Ende des Testzeitraums" : "the end of the trial";
        return german
            ? $"Der Testzeitraum endet am {when}. An diesem Tag wird die erste Laufzeit von {Weeks(weeks, true)}{Price(first, " zu ")} berechnet, danach alle {Weeks(next, true)}{Price(renewal, " zu ")} bis zur Kündigung. Wenn Sie vor diesem Zeitpunkt kündigen, wird nichts berechnet."
            : $"The trial ends on {when}. On that date the first term of {Weeks(weeks, false)} is charged{Price(first, " at ")}, then every {Weeks(next, false)}{Price(renewal, " at ")} until cancelled. If you cancel before then, nothing is charged.";
    }

    /// <summary>The billing period row of the overview; null when the event is no subscription payment.</summary>
    public static (string Label, string Value)? BillingPeriod(PaymentEvent payment, bool german) =>
        IsSubscription(payment)
            ? (german ? "Abrechnungszeitraum" : "Billing period", Weeks(payment.SubscriptionBillingWeeks.Value, german))
            : null;

    /// <summary>Renewal terms and how to cancel; empty when the event is no subscription payment.</summary>
    public static string[] Notes(PaymentEvent payment, bool german)
    {
        if (!IsSubscription(payment))
            return [];
        var cancel = CancelLine(payment, german);
        if (IsTrialStart(payment))
            return [cancel];
        var weeks = payment.SubscriptionBillingWeeks.Value;
        var next = payment.SubscriptionIsExtension == true
            ? ExtensionSentence(payment, weeks, german)
            : IsCompleteTermSwitch(payment)
                ? TermSwitchSentence(payment, weeks, german)
                : IsCoflnetContinuation(payment)
                    ? ContinuationSentence(payment, weeks, german)
                    : ProviderSentence(payment, german);
        var lines = new List<string> { next };
        if (PromotionLine(payment, weeks, german) is { } promotion)
            lines.Add(promotion);
        lines.Add(cancel);
        if (IsFirstPaymentAfterTrial(payment))
            lines.Insert(0, TrialEndedLine(payment, german));
        return [.. lines];
    }

    private static string TrialEndedLine(PaymentEvent payment, bool german)
    {
        var end = FormatUtc(payment.SubscriptionTrialEndsAt);
        return german ? $"Ihr kostenloser Testzeitraum endete am {end}." : $"Your free trial ended on {end}.";
    }

    private static string CancelLine(PaymentEvent payment, bool german)
    {
        var cancelUrl = german ? CancelUrlDe : CancelUrlEn;
        var accountUrl = AccountUrlFor(payment);
        return german
            ? $"Jederzeit kündbar: in Ihrem Konto unter {accountUrl} oder über die Kündigungsseite {cancelUrl}"
            : $"Cancel anytime: in your account at {accountUrl} or via the cancellation page {cancelUrl}";
    }

    /// <summary>A term switch with every detail the switch wording needs; otherwise the normal subscription wording is used.</summary>
    internal static bool IsCompleteTermSwitch(PaymentEvent payment) =>
        payment.SubscriptionIsTermSwitch == true
        && payment.SubscriptionPreviousBillingWeeks is > 0
        && payment.SubscriptionPreviousAccessEndsAt.HasValue
        && payment.ServiceEndsAtUtc.HasValue
        && payment.SubscriptionNextBillingAt.HasValue;

    private static string TermSwitchSentence(PaymentEvent payment, int weeks, bool german)
    {
        var previous = payment.SubscriptionPreviousBillingWeeks.Value;
        var previousEnd = FormatUtc(payment.SubscriptionPreviousAccessEndsAt);
        var newEnd = FormatUtc(payment.ServiceEndsAtUtc);
        var switched = german
            ? $"Sie haben von {Weeks(previous, true)} auf {Weeks(weeks, true)} gewechselt. Ihre bereits bezahlten Tage bis {previousEnd} bleiben erhalten; Ihr Zugang läuft nun bis {newEnd}."
            : $"You switched from {Weeks(previous, false)} to {Weeks(weeks, false)}. Your remaining paid days until {previousEnd} are kept; your access now runs until {newEnd}.";
        if (!IsCoflnetContinuation(payment))
            return $"{switched} {ProviderSentence(payment, german)}";
        var next = payment.SubscriptionRenewalWeeks is > 0 ? payment.SubscriptionRenewalWeeks.Value : DefaultRenewalWeeks;
        var amount = TaxStatement.RenewalAmount(payment, german);
        var from = FormatUtc(payment.SubscriptionNextBillingAt);
        return german
            ? $"{switched} Ab {from} läuft es alle {Weeks(next, true)}{Price(amount, " zu ")} weiter bis zur Kündigung."
            : $"{switched} From {from} it continues every {Weeks(next, false)}{Price(amount, " at ")} until cancelled.";
    }

    private static string PromotionLine(PaymentEvent payment, int weeks, bool german)
    {
        if (!IsCompleteTermSwitch(payment) || string.IsNullOrWhiteSpace(payment.SubscriptionPromotionCode))
            return null;
        return german
            ? $"Aktionspreis für die ersten {Weeks(weeks, true)}."
            : $"Sale price for the first {Weeks(weeks, false)}.";
    }

    private static string FormatUtc(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Direct Stripe/PayPal and Managed Payments subscriptions continue as Coflnet's own 4-week plans.</summary>
    private static bool IsCoflnetContinuation(PaymentEvent payment)
    {
        var provider = payment.PaymentProvider ?? "";
        return provider.Equals("stripe", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("paypal", StringComparison.OrdinalIgnoreCase);
    }

    private static string ContinuationSentence(PaymentEvent payment, int weeks, bool german)
    {
        var next = payment.SubscriptionRenewalWeeks is > 0
            ? payment.SubscriptionRenewalWeeks.Value
            : DefaultRenewalWeeks;
        var amount = TaxStatement.RenewalAmount(payment, german);
        var repeats = next == weeks || payment.SubscriptionIsRenewal == true;
        if (repeats)
            return german
                ? $"Das Abonnement verlängert sich alle {Weeks(next, true)}{Price(amount, " zu ")} bis zur Kündigung."
                : $"This subscription renews every {Weeks(next, false)}{Price(amount, " at ")} until cancelled.";
        return german
            ? $"Nach den ersten {Weeks(weeks, true)} läuft das Abonnement alle {Weeks(next, true)} zum {next}-Wochen-Preis{Price(amount, " von ")} weiter bis zur Kündigung."
            : $"After the first {Weeks(weeks, false)} it continues every {Weeks(next, false)} at the {next}-week price{Price(amount, " of ")} until cancelled.";
    }

    private static string ExtensionSentence(PaymentEvent payment, int weeks, bool german)
    {
        var renewed = german
            ? $"Das Abonnement wurde auf Ihren Wunsch um eine weitere Laufzeit von {Weeks(weeks, true)} verlängert."
            : $"This subscription was renewed for another term of {Weeks(weeks, false)}, as you requested.";
        if (!IsCoflnetContinuation(payment))
            return $"{renewed} {ProviderSentence(payment, german)}";
        var next = payment.SubscriptionRenewalWeeks is > 0 ? payment.SubscriptionRenewalWeeks.Value : DefaultRenewalWeeks;
        var amount = TaxStatement.RenewalAmount(payment, german);
        return german
            ? $"{renewed} Danach läuft es alle {Weeks(next, true)}{Price(amount, " zu ")} weiter bis zur Kündigung."
            : $"{renewed} Afterwards it continues every {Weeks(next, false)}{Price(amount, " at ")} until cancelled.";
    }

    private static string ProviderSentence(PaymentEvent payment, bool german)
    {
        var provider = PaymentDisplayNames.Provider(payment.PaymentProvider);
        return german
            ? $"Verlängerung und Abrechnung richten sich nach den Checkout-Bedingungen von {provider}."
            : $"Renewal and billing follow the checkout terms of {provider}.";
    }

    internal static string Price(string amount, string before) =>
        amount.Length == 0 ? "" : before + amount;

    internal static string Weeks(int weeks, bool german) =>
        german ? $"{weeks} {(weeks == 1 ? "Woche" : "Wochen")}" : $"{weeks} {(weeks == 1 ? "week" : "weeks")}";
}
