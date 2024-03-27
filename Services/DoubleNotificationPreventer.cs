using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Coflnet.Sky.EventBroker.Services;
public class DoubleNotificationPreventer
{

    private ConcurrentDictionary<string, ConcurrentQueue<int>> LastNotifications = new ();

    public bool HasNeverBeenSeen(string userId, string referenc)
    {
        if (LastNotifications.TryGetValue(userId, out ConcurrentQueue<int> queue))
        {
            if (queue.Contains(referenc.GetHashCode()))
                return false;

        }
        else
        {
            queue = new ConcurrentQueue<int>();
            LastNotifications.AddOrUpdate(userId, queue, (id, queue) => queue);
        }
        queue.Enqueue(referenc.GetHashCode());
        if (queue.Count > 15)
            queue.TryDequeue(out int a);
        return true;
    }
}

