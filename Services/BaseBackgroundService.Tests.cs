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

    /// <summary>A missing, null, empty or malformed whitelist must not drop the notification and adds no tag suffix</summary>
    [TestCase("null", "1")]
    [TestCase("", "1")]
    [TestCase("{\"Tags\":[\"a\",\"b\"]}", "1;a,b")]
    [TestCase("{not json", "1")]
    public async Task WhitelistDataExtendsSubIdOrIsIgnored(string whitelist, string expectedSubId)
    {
        var data = new Dictionary<string, string> { ["userId"] = UserId, ["subId"] = "1", ["whitelist"] = whitelist };

        Assert.That(await CountWebhookDeliveries(data, expectedSubId), Is.EqualTo(1));
    }

    /// <summary>A notification without subId is still delivered with an empty subId, whitelist tags included</summary>
    [Test]
    public async Task NotificationWithoutSubIdIsDeliveredWithEmptySubId()
    {
        var data = new Dictionary<string, string> { ["userId"] = UserId, ["whitelist"] = "{\"Tags\":[\"a\",\"b\"]}" };

        // an empty filter only matches an empty SourceSubId
        Assert.That(await CountWebhookDeliveries(data, subIdRegex: ""), Is.EqualTo(1));
    }

    /// <summary>Processes one notification and counts the webhook calls of a subscription filtered by <paramref name="subIdRegex"/></summary>
    private static async Task<int> CountWebhookDeliveries(Dictionary<string, string> data, string subIdRegex)
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
        await AddFlipSubscription(db, address, subIdRegex);

        await ProcessNotification(CreateBackgroundService(db), new FirebaseNotification("New flip", "Costs 1m",
            "https://sky.coflnet.com/auction/a", null, null, data));

        webhook.Stop();
        return received;
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

    private static async Task AddFlipSubscription(EventDbContext db, string address, string subIdRegex = null)
    {
        db.Subscriptions.Add(new Subscription
        {
            UserId = UserId,
            SourceSubIdRegex = subIdRegex,
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
