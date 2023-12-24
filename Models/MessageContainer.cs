
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Coflnet.Sky.EventBroker.Models
{
    public class MessageContainer
    {
        public int Id { get; set; }
        /// <summary>
        /// Unique reference for this message
        /// </summary>
        /// <value></value>
        [MaxLength(32)]
        public string Reference { get; set; }
        public string Message { get; set; }
        public string Summary { get; set; }
        public string Link { get; set; }
        public string ImageLink { get; set; }
        /// <summary>
        /// What event the message originated from
        /// </summary>
        /// <value></value>
        public string SourceType { get; set; }
        /// <summary>
        /// Optional Sub id within <see cref="SourceType"/>
        /// identifies the event more precisely
        /// </summary>
        public string SourceSubId {get;set;}
        [NotMapped]
        public object Data { get; set; }
        public User User { get; set; }
        public Settings Setings { get; set; }

        [Timestamp] // auto set on insert or update
        public DateTime? Timestamp { get; set; }
    }
}