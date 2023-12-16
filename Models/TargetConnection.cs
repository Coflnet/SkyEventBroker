using System.Text.Json.Serialization;

namespace Coflnet.Sky.EventBroker.Models;

public class TargetConnection
{
    public int Id { get; set; }
    [JsonIgnore]
    public Subscription Subscription { get; set; }
    public NotificationTarget Target { get; set; }
    public int Priority { get; set; }
    public bool IsDisabled { get; set; }

}