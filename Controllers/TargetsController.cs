using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Sky.EventBroker.Models;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace Coflnet.Sky.EventBroker.Controllers
{
    /// <summary>
    /// Endpoints for managing notification targets
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class TargetsController : ControllerBase
    {
        private EventDbContext context;

        /// <summary>
        /// Creates a new instance of <see cref="NotificationTarget"/>
        /// </summary>
        /// <param name="context"></param>
        public TargetsController(EventDbContext context)
        {
            this.context = context;
        }


        /// <summary>
        /// Returns the notification targets for an user
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("user/{userId}")]
        public async Task<IEnumerable<NotificationTarget>> GetMessages(string userId)
        {
            return await context.NotificationTargets.Where(n => n.UserId == userId).ToListAsync();
        }
        /// <summary>
        /// Adds a notification target for an user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("user/{userId}")]
        public async Task CreateNotification(string userId, [FromBody] NotificationTarget target)
        {
            target.UserId = userId;
            context.NotificationTargets.Add(target);
            await context.SaveChangesAsync();
        }
        /// <summary>
        /// Deletes a notification target for an user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        [HttpDelete]
        [Route("user/{userId}")]
        public async Task DeleteNotification(string userId, [FromBody] NotificationTarget target)
        {
            context.NotificationTargets.Remove(target);
            await context.SaveChangesAsync();
        }
        /// <summary>
        /// Updates a notification target for an user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        [HttpPut]
        [Route("user/{userId}")]
        public async Task UpdateNotification(string userId, [FromBody] NotificationTarget target)
        {
            context.NotificationTargets.Update(target);
            await context.SaveChangesAsync();
        }
    }
}
