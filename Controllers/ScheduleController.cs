using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Coflnet.Sky.EventBroker.Services;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.EventBroker.Controllers;
/// <summary>
/// Future notifications
/// </summary>
[ApiController]
[Route("[controller]")]
public class ScheduleController : ControllerBase
{
    private readonly ScheduleService service;

    /// <summary>
    /// Creates a new instance of <see cref="ScheduleController"/>
    /// </summary>
    public ScheduleController(ScheduleService service)
    {
        this.service = service;
    }

    /// <summary>
    /// Schedules a message for a user at a specific time
    /// </summary> 
    [HttpPost]
    [Route("{userId}")]
    public async Task AddMessage(string userId, [FromBody] MessageContainer msg, [FromQuery] DateTime scheduledTime)
    {
        if (msg.User == null)
            msg.User = new User();
        msg.User.UserId = userId;
        await service.AddMessage(msg, scheduledTime, userId);
    }

    /// <summary>
    /// Gets all scheduled messages for a user
    /// </summary>
    /// <param name="userId"></param>
    /// <returns></returns>
    [HttpGet]
    [Route("{userId}")]
    public async Task<List<MessageSchedule>> GetScheduledMessages(string userId)
    {
        return await service.GetScheduledMessages(userId);
    }

    /// <summary>
    /// Deletes a scheduled message
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="id"></param>
    /// <returns></returns>
    [HttpDelete]
    [Route("{userId}/{id}")]
    public async Task DeleteScheduledMessage(string userId, int id)
    {
        await service.RemoveMessage(userId, id);
    }
}

