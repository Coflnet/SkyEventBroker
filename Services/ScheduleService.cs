using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Coflnet.Sky.EventBroker.Services;
public class ScheduleService : BackgroundService
{
    private ILogger<ScheduleService> logger;
    // service scope
    private readonly IServiceScopeFactory scopeFactory;

    public ScheduleService(ILogger<ScheduleService> logger, IServiceScopeFactory scopeFactory)
    {
        this.logger = logger;
        this.scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            using var scope = scopeFactory.CreateScope();
            using var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
            var now = DateTime.UtcNow;
            var messages = await db.ScheduledMessages.Where(m => m.ScheduledTime < now)
                .Include(s => s.Message).ThenInclude(m => m.User)
                .Include(m => m.Message).ThenInclude(m => m.Setings)
                .ToListAsync();
            if(messages.Count == 0)
                continue;
            foreach (var message in messages)
            {
                var messageService = scope.ServiceProvider.GetRequiredService<MessageService>();
                await messageService.AddMessage(message.Message);
            }
            db.ScheduledMessages.RemoveRange(messages);
            await db.SaveChangesAsync();
            logger.LogInformation($"Sent {messages.Count} messages");
        }
    }

    public async Task<MessageSchedule> AddMessage(MessageContainer message, DateTime scheduledTime, string userId)
    {
        var schedule = new MessageSchedule()
        {
            Message = message,
            ScheduledTime = scheduledTime,
            UserId = userId
        };
        message.Timestamp = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
        await db.ScheduledMessages.AddAsync(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    public async Task<List<MessageSchedule>> GetScheduledMessages(string userId)
    {
        using var scope = scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
        return await db.ScheduledMessages.Where(m => m.UserId == userId)
                .Include(m => m.Message).ThenInclude(m => m.Setings).ToListAsync();
    }

    public async Task RemoveMessage(string userId, int id)
    {
        using var scope = scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
        var message = await db.ScheduledMessages.FindAsync(id);
        if (message.UserId != userId)
        {
            throw new UnauthorizedAccessException();
        }
        db.ScheduledMessages.Remove(message);
        await db.SaveChangesAsync();
    }
}
