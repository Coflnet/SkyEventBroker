using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Every firebase target got auto disabled while the legacy fcm api was returning 404.
/// Once when REENABLE_DISABLED_FIREBASE_TARGETS is set they are activated again,
/// tokens that are actually dead get disabled again on the next send attempt.
/// </summary>
public class FirebaseTargetReactivationService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration config;
    private readonly ILogger<FirebaseTargetReactivationService> logger;

    /// <summary>
    /// Creates a new instance of <see cref="FirebaseTargetReactivationService"/>
    /// </summary>
    public FirebaseTargetReactivationService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<FirebaseTargetReactivationService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.config = config;
        this.logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["REENABLE_DISABLED_FIREBASE_TARGETS"]?.ToLower() != "true")
            return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
        var reactivated = await db.Set<TargetConnection>()
            .Where(c => c.IsDisabled && c.Target.Type == NotificationTarget.TargetType.FIREBASE)
            .ExecuteUpdateAsync(c => c.SetProperty(t => t.IsDisabled, false), stoppingToken);
        logger.LogInformation("reactivated {count} disabled firebase targets", reactivated);
    }
}
