using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace Coflnet.Sky.EventBroker.Models
{
    public class User
    {
        [JsonIgnore]
        public int Id { get; set; }
        [MaxLength(40)]
        public string UserName { get; set; }
        [MaxLength(36)]
        public string UserId { get; set; }
        [MaxLength(8)]
        public string Locale { get; set; }
    }
}