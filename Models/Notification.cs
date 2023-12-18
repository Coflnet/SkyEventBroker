using System.Collections.Generic;
using Newtonsoft.Json;

namespace Coflnet.Sky.EventBroker.Models;
public class FirebaseNotification
{
    public string title;
    [JsonIgnore]
    public Dictionary<string, string> data;
    public string click_action;
    public string icon;
    public string image;
    public string body;

    public FirebaseNotification(string title, string body, string click_action, string icon, string image, Dictionary<string, string> data)
    {
        this.title = title;
        this.data = data;
        this.click_action = click_action;
        this.icon = icon;
        this.image = image;
        this.body = body;
    }

    public override bool Equals(object obj)
    {
        return obj is FirebaseNotification notification &&
               title == notification.title &&
               data == notification.data &&
               click_action == notification.click_action &&
               icon == notification.icon &&
               image == notification.image &&
               body == notification.body;
    }

    public override int GetHashCode()
    {
        int hashCode = 246475487;
        hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(title);
        hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(click_action);
        hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(body);
        return hashCode;
    }
}
