using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Sky.EventBroker.Models;

public class PurchaseConfirmationDelivery
{
    public long Id { get; set; }

    [Required]
    [MaxLength(32)]
    public string Reference { get; set; }

    [MaxLength(320)]
    public string Recipient { get; set; }

    [MaxLength(16)]
    public string Locale { get; set; }

    public string Payload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Set when a row needs an operator: it failed permanently (mismatching terms, rejected
    /// recipient) or kept failing transiently for the max row age (see
    /// <c>PurchaseConfirmationDeliveryService</c>). Parked rows are excluded from the claim
    /// query until requeued via the operator endpoint.
    /// </summary>
    public DateTime? FailedAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntil { get; set; }

    /// <summary>Formatted as <c>[reason] Type: message</c>.</summary>
    [MaxLength(2000)]
    public string LastError { get; set; }
}
