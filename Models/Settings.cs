using Newtonsoft.Json;

namespace Coflnet.Sky.EventBroker.Models
{
    /// <summary>
    /// Special settings how this message should be handeled
    /// </summary>
    public class Settings
    {
        /// <summary>
        /// Primary Key for database
        /// </summary>
        /// <value></value>
        [JsonIgnore]
        public int Id { get; set; }
        /// <summary>
        /// Report back the the message was sent (to prevent notifications)
        /// </summary>
        /// <value></value>
        public bool ConfirmDelivery { get; set; }
        /// <summary>
        /// Play a sound when the message is sent
        /// </summary>
        /// <value></value>
        public bool PlaySound { get; set; }
        /// <summary>
        /// Should this message be stored until the user comes online again?
        /// </summary>
        /// <value></value>
        public bool StoreIfOffline { get; set; }
    }
}