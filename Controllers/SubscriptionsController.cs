using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Sky.EventBroker.Models;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Coflnet.Sky.Core;
using System;

namespace Coflnet.Sky.EventBroker.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SubscriptionsController : ControllerBase
    {
        private EventDbContext context;

        /// <summary>
        /// Creates a new instance of <see cref="NotificationTarget"/>
        /// </summary>
        /// <param name="context"></param>
        public SubscriptionsController(EventDbContext context)
        {
            this.context = context;
        }
        [HttpGet]
        public async Task<IEnumerable<PublicSubscription>> GetSubscriptions(string userId)
        {
            var dbVersion = await context.Subscriptions.Where(n => n.UserId == userId).Include(s=>s.Targets).ThenInclude(t=>t.Target).ToListAsync();
            return dbVersion.Select(s => new PublicSubscription(s));
        }
        [HttpPost]
        public async Task<PublicSubscription> CreateSubscription(string userId, [FromBody] PublicSubscription target)
        {
            target.Id = 0;
            var subscription = new Subscription()
            {
                SourceType = target.SourceType.ToString(),
                UserId = userId,
                Id = target.Id,
                Targets = target.Targets.Select(t => new TargetConnection()
                {
                    Target = context.NotificationTargets.Where(nt=>nt.UserId == userId && nt.Name == t.Name).FirstOrDefault(),
                    Priority = t.Priority
                }).ToList()
            };
            context.Subscriptions.Add(subscription);
            await context.SaveChangesAsync();
            return new PublicSubscription(subscription);
        }
        [HttpDelete]
        public async Task DeleteSubscription(string userId, [FromBody] PublicSubscription target)
        {
            var current = await context.Subscriptions.Include(s=>s.Targets).FirstAsync(t=>t.Id == target.Id);
            if (current == null)
                return;
            AssertSameUser(userId, current);
            context.Subscriptions.Remove(current);
            foreach (var item in current.Targets)
            {
                context.Remove(item);
            }
            await context.SaveChangesAsync();
        }
        [HttpPut]
        public async Task<PublicSubscription> UpdateSubscription(string userId, [FromBody] PublicSubscription target)
        {
            var current = await context.Subscriptions.Include(s=>s.Targets).ThenInclude(t=>t.Target).FirstOrDefaultAsync(t=>t.Id == target.Id);
            if (current == null)
                return null;
            AssertSameUser(userId, current);
            foreach (var item in target.Targets)
            {
                AddOrUpdateTarget(userId, current, item);
            }
            // remove targets
            foreach (var item in current.Targets.ToList())
            {
                if (!target.Targets.Any(t=>t.Name == item.Target.Name))
                {
                    current.Targets.Remove(item);
                }
            }
            context.Subscriptions.Update(current);
            await context.SaveChangesAsync();
            return new PublicSubscription(current);
        }

        private void AddOrUpdateTarget(string userId, Subscription current, PublicSubscription.TargetLink item)
        {
            var currentTarget = current.Targets.FirstOrDefault(t => t.Target.Name == item.Name);
            if (currentTarget == null)
            {
                // add new target
                current.Targets.Add(new TargetConnection()
                {
                    Target = context.NotificationTargets.Where(t => t.UserId == userId && t.Name == item.Name).FirstOrDefault(),
                    Priority = item.Priority,
                    IsDisabled = item.IsDisabled
                });
            }
            else
            {
                // update existing target
                currentTarget.Priority = item.Priority;
                currentTarget.IsDisabled = item.IsDisabled;
            }
        }

        private static void AssertSameUser(string userId, Subscription current)
        {
            if (current.UserId != userId)
                throw new CoflnetException("not_authorized", "You are not allowed to edit this subscription");
        }
    }
}
