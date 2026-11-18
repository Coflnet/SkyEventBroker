using System;
using System.Globalization;
using Coflnet.Sky.EventBroker.Models;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// The "what happens next / how to cancel" section of a subscription purchase confirmation.
/// </summary>
internal static class SubscriptionTermsSection
{
    public const string AccountUrl = "https://sky.coflnet.com/account";
    private const string CancelUrlEn = "https://coflnet.com/cancel-contract";
    private const string CancelUrlDe = "https://coflnet.com/de/cancel-contract";
    private const int DefaultRenewalWeeks = 4;

    public static bool IsSubscription(PaymentEvent payment) =>
        payment.SubscriptionBillingWeeks is > 0;

    /// <summary>Empty when the event is no subscription payment.</summary>
    public static string Build(PaymentEvent payment, bool german)
    {
        if (!IsSubscription(payment))
            return "";
        var weeks = payment.SubscriptionBillingWeeks.Value;
        var next = IsCoflnetContinuation(payment)
            ? ContinuationSentence(payment, weeks, german)
            : ProviderSentence(payment, german);
        var cancelUrl = german ? CancelUrlDe : CancelUrlEn;
        return german
            ? $"""

ABONNEMENT
----------
Abrechnungszeitraum: {Weeks(weeks, true)}
{next}
Jederzeit kündbar: in Ihrem Konto unter {AccountUrl} oder über die Kündigungsseite {cancelUrl}

"""
            : $"""

SUBSCRIPTION
------------
Billing period: {Weeks(weeks, false)}
{next}
Cancel anytime: in your account at {AccountUrl} or via the cancellation page {cancelUrl}

""";
    }

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
        var amount = RenewalAmount(payment);
        var repeats = next == weeks || payment.SubscriptionIsRenewal == true;
        if (repeats)
            return german
                ? $"Das Abonnement verlängert sich alle {Weeks(next, true)}{Price(amount, " zu ")} bis zur Kündigung."
                : $"This subscription renews every {Weeks(next, false)}{Price(amount, " at ")} until cancelled.";
        return german
            ? $"Nach den ersten {Weeks(weeks, true)} läuft das Abonnement alle {Weeks(next, true)} zum {next}-Wochen-Preis weiter{Price(amount, " (", ")")} bis zur Kündigung."
            : $"After the first {Weeks(weeks, false)} it continues every {Weeks(next, false)} at the {next}-week price{Price(amount, " (", ")")} until cancelled.";
    }

    private static string ProviderSentence(PaymentEvent payment, bool german)
    {
        var provider = payment.PaymentProvider ?? "";
        return german
            ? $"Verlängerung und Abrechnung richten sich nach den Checkout-Bedingungen von {provider}."
            : $"Renewal and billing follow the checkout terms of {provider}.";
    }

    private static string RenewalAmount(PaymentEvent payment) =>
        payment.SubscriptionRenewalAmount is > 0
            ? $"{payment.SubscriptionRenewalAmount.Value.ToString("0.00##", CultureInfo.InvariantCulture)} {payment.Currency}".Trim()
            : "";

    private static string Price(string amount, string before, string after = "") =>
        amount.Length == 0 ? "" : $"{before}{amount}{after}";

    private static string Weeks(int weeks, bool german) =>
        german ? $"{weeks} {(weeks == 1 ? "Woche" : "Wochen")}" : $"{weeks} {(weeks == 1 ? "week" : "weeks")}";
}
