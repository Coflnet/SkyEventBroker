
using System;

namespace Coflnet.Sky.EventBroker.Models;
public class MessageSchedule
{
    public int Id { get; set; }
    public string UserId { get; set; }
    public DateTime ScheduledTime { get; set; }
    public MessageContainer Message { get; set; }
}
