using System;

namespace Coflnet.Sky.EventBroker.Models;

public class PaymentEvent
{
    public double PayedAmount { get; set; }
    public string ProductId { get; set; }

    /// <summary>Human readable product title. Optional: events of older producers omit it and the email falls back to <see cref="ProductId"/>.</summary>
    public string ProductTitle { get; set; }
    public string UserId { get; set; }
    public string Email { get; set; }
    public string Currency { get; set; }
    public string PaymentMethod { get; set; }
    public string PaymentProvider { get; set; }
    public string PaymentProviderTransactionId { get; set; }
    public DateTime Timestamp { get; set; }
    public string ConfirmationType { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public decimal? CoinAmount { get; set; }
    public DateTime? ServiceStartsAtUtc { get; set; }
    public DateTime? ServiceEndsAtUtc { get; set; }
    public string DeclarationVersion { get; set; }
    public string DeclarationText { get; set; }
    public string LegalLocale { get; set; }
    public string AgreementId { get; set; }
    public string AgreementHash { get; set; }
    public string TermsVersion { get; set; }
    public string TermsAcceptanceHash { get; set; }
    public string WithdrawalVersion { get; set; }
    public string WithdrawalSha256 { get; set; }
    public string TaxCountry { get; set; }
    public string ConsumerRightsRegime { get; set; }
    public int VatRateBasisPoints { get; set; }
    public long GrossEurCents { get; set; }
    public long VatEurCents { get; set; }
    public string OrderDetailsJson { get; set; }

    // Optional subscription terms. Payments does not send these yet; without them the
    // confirmation email contains no subscription section.

    /// <summary>Billing period in weeks (4, 13 or 52) of the term this payment paid. Its presence marks the payment as a subscription payment.</summary>
    public int? SubscriptionBillingWeeks { get; set; }

    /// <summary>Weeks of the cycles that follow this term (4 for the 4-week continuation, or the billing weeks for plans that repeat their term).</summary>
    public int? SubscriptionRenewalWeeks { get; set; }

    /// <summary>What each following cycle charges, in <see cref="Currency"/>.</summary>
    public decimal? SubscriptionRenewalAmount { get; set; }

    /// <summary>True when this payment renews an already running subscription instead of starting it.</summary>
    public bool? SubscriptionIsRenewal { get; set; }

    /// <summary>
    /// How tax relates to <see cref="PayedAmount"/>: "included", "net" (the merchant of record adds the tax) or "none".
    /// Null on events of older producers and one-time purchases: the email then makes no claim about tax.
    /// </summary>
    public string TaxTreatment { get; set; }

    /// <summary>The tax contained in <see cref="PayedAmount"/>, in <see cref="Currency"/> (only with TaxTreatment "included").</summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>How tax relates to <see cref="SubscriptionRenewalAmount"/>, like <see cref="TaxTreatment"/>.</summary>
    public string RenewalTaxTreatment { get; set; }

    /// <summary>Renewal reminders only: when the term the subscription currently runs on (13 or 52 weeks) ends and it falls back to 4-week billing.</summary>
    public DateTime? SubscriptionTermEndsAt { get; set; }

    /// <summary>Renewal reminders only: what renewing for another <see cref="SubscriptionBillingWeeks"/> weeks costs, in <see cref="Currency"/>. Null: no extension is offered.</summary>
    public decimal? SubscriptionExtensionAmount { get; set; }

    /// <summary>Renewal reminders only: identifies the subscription in the extend link of the email.</summary>
    public string SubscriptionExternalId { get; set; }

    /// <summary>True when this payment is the first of a term the buyer asked to extend (instead of falling back to 4-week billing).</summary>
    public bool? SubscriptionIsExtension { get; set; }
}
