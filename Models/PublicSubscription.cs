using System;
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
        if(!Enum.TryParse<SourceType>(s.SourceType, out var type))
        {
            Console.WriteLine($"Unknown SourceType {s.SourceType}");
            type = SourceType.Unknwon;
        }
        SourceType = type;
        SourceSubIdRegex = s.SourceSubIdRegex;
        Targets = new List<TargetLink>();
        foreach (var t in s.Targets)
        {
            Targets.Add(new TargetLink
            {
                Name = t.Target?.Name,
                Id = t.Target.Id,
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
    /// </summary>
    public SourceType SourceType { get; set; }
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
        public string? Name { get; set; }
        public int Id { get; set; }
        public int Priority { get; set; }
        public bool IsDisabled { get; set; }
    }

}

public enum SourceType
{
    /// <summary>
    /// Unknown event
    /// </summary>
    Unknwon,
    /// <summary>
    /// Any event
    /// </summary>
    Any,
    /// <summary>
    /// outbid/undercut
    /// </summary>
    Bazaar,
    /// <summary>
    /// Order expired (after 7 days)
    /// </summary>
    BazaarExpire,
    /// <summary>
    /// Price notifications from Subscription listeners
    /// </summary>
    Subscription,
    /// <summary>
    /// A product or service was purchased via payment system
    /// </summary>
    Purchase,
    /// <summary>
    /// Minecraft account was verified
    /// </summary>
    McVerify
}