using System;
using System.Collections.Generic;
using System.Globalization;
using Coflnet.Sky.EventBroker.Models;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Readable product, provider and payment method names for the purchase confirmation email.
/// </summary>
internal static class PaymentDisplayNames
{
    private static readonly Dictionary<string, string> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paypal"] = "PayPal",
        ["stripe"] = "Stripe",
        ["lemonsqueezy"] = "Lemon Squeezy",
        ["coingate"] = "CoinGate",
        ["coflcoins"] = "CoflCoins",
        ["googlepay"] = "Google Play",
        ["Google Play"] = "Google Play"
    };

    private static readonly Dictionary<string, (string En, string De)> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["card"] = ("Card", "Karte"),
        ["paypal"] = ("PayPal", "PayPal"),
        ["googlepay"] = ("Google Pay", "Google Pay"),
        ["google_pay"] = ("Google Pay", "Google Pay"),
        ["apple_pay"] = ("Apple Pay", "Apple Pay"),
        ["link"] = ("Link", "Link"),
        ["klarna"] = ("Klarna", "Klarna"),
        ["ideal"] = ("iDEAL", "iDEAL"),
        ["sepa_debit"] = ("SEPA direct debit", "SEPA-Lastschrift"),
        ["sofort"] = ("Sofort", "Sofort"),
        ["stripe_managed_payments"] = ("Stripe Managed Payments", "Stripe Managed Payments"),
        ["CoflCoin balance"] = ("CoflCoin balance", "CoflCoin-Guthaben"),
        ["crypto"] = ("Cryptocurrency", "Kryptowährung")
    };

    /// <summary>The product title, or the internal id for events of producers that do not send titles.</summary>
    public static string Product(PaymentEvent payment) =>
        string.IsNullOrWhiteSpace(payment.ProductTitle)
            ? payment.ProductId
            : payment.ProductTitle.Trim();

    public static string Provider(string provider) =>
        string.IsNullOrWhiteSpace(provider)
            ? ""
            : Providers.TryGetValue(provider.Trim(), out var name) ? name : Humanize(provider);

    /// <summary>Empty when the method is unknown or would only repeat the provider.</summary>
    public static string Method(PaymentEvent payment, bool german)
    {
        var method = payment.PaymentMethod?.Trim();
        if (string.IsNullOrEmpty(method) || method.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "";
        var name = Methods.TryGetValue(method, out var known)
            ? german ? known.De : known.En
            : Humanize(method);
        return name.Equals(Provider(payment.PaymentProvider), StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    private static string Humanize(string slug)
    {
        var text = slug.Trim().Replace('_', ' ');
        return char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }
}
