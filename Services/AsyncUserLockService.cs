using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Coflnet.Sky.EventBroker.Services
{
    public class AsyncUserLockService
    {
        private ConcurrentDictionary<string,SemaphoreSlim> locks = new();

        public async Task GetLock(string userId, Func<string,Task> todo)
        {
            var userLock = locks.GetOrAdd(userId ?? "", new SemaphoreSlim(1,1));
            try
            {
                await userLock.WaitAsync();
                await todo(userId);
            }
            finally
            {
                userLock.Release();
            }
        }
    }
}
