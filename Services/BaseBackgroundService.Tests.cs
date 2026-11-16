using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>Tests of the notification consumer chain in <see cref="BaseBackgroundService"/></summary>
public class BaseBackgroundServiceTests
{
    private const string UserId = "flip-user";

    /// <summary>Flips of one item must not be de-duplicated against each other before reaching the webhook</summary>
    [Test]
    public async Task WhitelistedFlipsOfTheSameItemAreAllSentToWebhook()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>().UseSqlite(connection).Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();

        using var webhook = StartWebhook(out var address);
        var received = 0;
        _ = Task.Run(async () =>
        {
            while (webhook.IsListening)
            {
                var request = await webhook.GetContextAsync();
                Interlocked.Increment(ref received);
                request.Response.StatusCode = (int)HttpStatusCode.NoContent;
                request.Response.Close();
            }
        });
        await AddFlipSubscription(db, address);
        var background = CreateBackgroundService(db);

        // titles as produced by SkySubscriptions for whitelisted flips; only the auction differs
        await ProcessNotification(background, Flip("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await ProcessNotification(background, Flip("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        Assert.That(received, Is.EqualTo(2));
        webhook.Stop();
    }

    private static HttpListener StartWebhook(out string address)
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        address = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
        port.Stop();
        var server = new HttpListener();
        server.Prefixes.Add(address);
        server.Start();
        return server;
    }

    private static async Task AddFlipSubscription(EventDbContext db, string address)
    {
        db.Subscriptions.Add(new Subscription
        {
            UserId = UserId,
            SourceType = SourceType.Subscription.ToString(),
            Targets = new List<TargetConnection>
            {
                // in-game delivery disabled, so only the webhook receives the flip
                Connect(NotificationTarget.TargetType.InGame, "ingame", NotificationTarget.NotifyWhen.NEVER),
                Connect(NotificationTarget.TargetType.DiscordWebhook, address, NotificationTarget.NotifyWhen.ALWAYS)
            }
        });
        await db.SaveChangesAsync();
    }

    private static TargetConnection Connect(NotificationTarget.TargetType type, string target, NotificationTarget.NotifyWhen when)
    {
        return new TargetConnection
        {
            Target = new NotificationTarget { Type = type, Target = target, When = when, UserId = UserId, Name = type.ToString() }
        };
    }

    private static FirebaseNotification Flip(string auctionId)
    {
        return new FirebaseNotification("New whitelisted auction for Hyperion", "Costs 800m",
            "https://sky.coflnet.com/auction/" + auctionId, "https://sky.coflnet.com/static/icon/HYPERION", null,
            new Dictionary<string, string> { ["userId"] = UserId, ["subId"] = "1" });
    }

    private static BaseBackgroundService CreateBackgroundService(EventDbContext db)
    {
        var messageService = new MessageService(db, connection: null, productsApi: null,
            logger: NullLogger<MessageService>.Instance, lockService: null, settingsService: null, config: null,
            premiumService: null, doubleNotificationPreventer: new DoubleNotificationPreventer(), pushService: null, userApi: null);
        var provider = new ServiceCollection().AddSingleton(messageService).BuildServiceProvider();
        return new BaseBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), NullLogger<BaseBackgroundService>.Instance);
    }

    private static Task ProcessNotification(BaseBackgroundService service, FirebaseNotification notification)
    {
        // the Kafka consumer hands each notification to this private step
        var method = typeof(BaseBackgroundService).GetMethod("ProcessNotification", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task)method.Invoke(service, new object[] { notification });
    }
}
