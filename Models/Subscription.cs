using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Sky.EventBroker.Models;

public class Subscription
{
    /// <summary>
    /// Primary Key for database
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// The user to subscribe for
    /// </summary>
    [MaxLength(36)]
    public string UserId { get; set; }
    /// <summary>
    /// The type of event to subscribe to
    /// * for all
    /// </summary>
    [MaxLength(100)]
    public string SourceType { get; set; }
    /// <summary>
    /// Regex to match the <see cref="MessageContainer.SourceSubId"/> against
    /// allows to narrow down which events to subscribe to
    /// </summary>
    [MaxLength(100)]
    public string SourceSubIdRegex { get; set; }
    /// <summary>
    /// Exact matching id in source type system
    /// </summary>
    [MaxLength(100)]
    public string SourceSubId { get; set; }
    /// <summary>
    /// The targets to send the notification to
    /// </summary>
    public List<TargetConnection> Targets { get; set; }
}
