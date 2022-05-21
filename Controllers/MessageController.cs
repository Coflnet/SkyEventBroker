using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.EventBroker.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using Coflnet.Sky.EventBroker.Services;

namespace Coflnet.Sky.EventBroker.Controllers
{
    /// <summary>
    /// Main Controller handling tracking
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class MessageController : ControllerBase
    {
        private readonly MessageService service;

        /// <summary>
        /// Creates a new instance of <see cref="MessageController"/>
        /// </summary>
        /// <param name="service"></param>
        public MessageController(MessageService service)
        {
            this.service = service;
        }

        /// <summary>
        /// Tracks a flip
        /// </summary>
        /// <param name="refence"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("confirm/{AuctionId}")]
        public async Task TrackFlip(string refence)
        {
            await service.Received(refence);
        }

        /// <summary>
        /// Tracks a flip
        /// </summary>
        /// <param name="refence"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("send/{userId}")]
        public async Task SendEvent(string userId, [FromBody] MessageContainer msg)
        {
            if (msg.User == null)
                msg.User = new User();
            msg.User.UserId = userId;
            await service.AddMessage(msg);
        }

        /// <summary>
        /// Tracks a flip
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("user/{userId}")]
        public async Task<IEnumerable<MessageContainer>> GetMessages(string userId)
        {
            return await service.GetMessages(userId);
        }
    }
}
