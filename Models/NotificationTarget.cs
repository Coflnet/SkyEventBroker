using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace Coflnet.Sky.EventBroker.Models
{
    public class NotificationTarget
    {
        /// <summary>
        /// Primary Key for database
        /// </summary>
        /// <value></value>
        [JsonIgnore]
        public int Id { get; set; }
        public string Target { get; set; }
        public TargetType Type { get; set; }
        public NotifyWhen When { get; set; }
        [MaxLength(36)]
        public string UserId { get; set; }
        /// <summary>
        /// User Given name of this target
        /// </summary>
        /// <value></value>
        [MaxLength(32)]
        public string Name { get; set; }
        public enum TargetType
        {
            UNKOWN,
            WEBHOOK,
            DISCORD,
            FIREBASE,
            EMAIL
        }

        public enum NotifyWhen
        {
            NEVER,
            AFTER_FAIL,
            ALWAYS
        }
    }
}