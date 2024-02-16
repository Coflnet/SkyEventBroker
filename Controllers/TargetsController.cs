using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Coflnet.Sky.EventBroker.Models;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Coflnet.Sky.EventBroker.Services;
using Coflnet.Sky.Core;
using User = Coflnet.Sky.EventBroker.Models.User;

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
        private MessageService messageService;

        /// <summary>
        /// Creates a new instance of <see cref="NotificationTarget"/>
        /// </summary>
        /// <param name="context"></param>
        /// <param name="messageService"></param>
        public TargetsController(EventDbContext context, MessageService messageService)
        {
            this.context = context;
            this.messageService = messageService;
        }


        /// <summary>
        /// Returns the notification targets for an user
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("{userId}")]
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
        [Route("{userId}")]
        public async Task<NotificationTarget> CreateNotification(string userId, [FromBody] NotificationTarget target)
        {
            target.UserId = userId;
            context.NotificationTargets.Add(target);
            await context.SaveChangesAsync();
            return target;
        }
        /// <summary>
        /// Deletes a notification target for an user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        [HttpDelete]
        [Route("{userId}")]
        public async Task DeleteNotification(string userId, [FromBody] NotificationTarget target)
        {
            target.UserId = userId;
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
        [Route("{userId}")]
        public async Task UpdateNotification(string userId, [FromBody] NotificationTarget target)
        {
            target.UserId = userId;
            context.NotificationTargets.Update(target);
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Sends a test notification to the target
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        /// <response code="200">Notification was sent</response>
        /// <response code="404">Target not found</response>
        /// <response code="500">Error while sending notification</response>
        [HttpPost]
        [Route("{userId}/test")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task SendTestNotification(string userId, [FromBody] NotificationTarget target)
        {
            var current = await context.NotificationTargets.FirstOrDefaultAsync(t => t.Id == target.Id);
            if (current == null)
                return;
            if (current.UserId != userId)
                throw new CoflnetException("not-authorized", "You are not authorized to access this target");

            await messageService.SendToTarget(new MessageContainer()
            {
                Message = "This is a test notification from sky.coflnet.com",
                Summary = "Test Notification",
                Link = "https://sky.coflnet.com",
                User = new User()
                {
                    UserId = userId
                },
            }, current);
        }
    }
}
