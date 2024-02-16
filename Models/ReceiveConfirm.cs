
using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Coflnet.Sky.EventBroker.Models
{
    public class ReceiveConfirm
    {
        [JsonIgnore]
        public int Id { get; set; }
        [MaxLength(32)]
        public string Reference {get;set;}
        [DataMember(Name = "timestamp")]
        public DateTime Timestamp { get; set; }
    }
}