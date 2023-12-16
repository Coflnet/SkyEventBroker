using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Sky.EventBroker.Models;

/// <summary>
/// Model exposed via the api
/// </summary>
public class PublicSubscription
{
    public PublicSubscription(Subscription s)
    {
        Id = s.Id;
        SourceType = s.SourceType;
        SourceSubIdRegex = s.SourceSubIdRegex;
        Targets = new List<TargetLink>();
        foreach (var t in s.Targets)
        {
            Targets.Add(new TargetLink
            {
                Name = t.Target?.Name,
                Priority = t.Priority,
                IsDisabled = t.IsDisabled
            });
        }
    }

    public PublicSubscription()
    {
    }

    /// <summary>
    /// Primary Key for database
    /// </summary>
    public int Id { get; set; }
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
    /// The targets to send the notification to
    /// </summary>
    public List<TargetLink> Targets { get; set; }

    public class TargetLink
    {
        public string Name { get; set; }
        public int Priority { get; set; }
        public bool IsDisabled { get; set; }
    }
}
