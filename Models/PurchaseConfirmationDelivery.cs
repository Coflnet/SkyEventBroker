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
    /// Set once a row has exhausted its retry budget (see
    /// <see cref="PurchaseConfirmationDeliveryService"/>'s max-attempts constant). Parked rows
    /// are excluded from the claim query instead of being retried forever.
    /// </summary>
    public DateTime? FailedAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntil { get; set; }

    [MaxLength(2000)]
    public string LastError { get; set; }
}
