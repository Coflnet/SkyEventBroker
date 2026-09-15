using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Prometheus;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>Consumes confirmed fills independently of Minecraft connections.</summary>
public class BazaarFillNotifications([FromKeyedServices("bazaar")] IConnectionMultiplexer redis, IServiceScopeFactory scopes,
    ILogger<BazaarFillNotifications> logger) : BackgroundService
{
    internal const string SourceName = "Coflnet.Sky.EventBroker.Bazaar";
    internal static readonly ActivitySource Traces = new(SourceName);
    internal static readonly Counter Deliveries = Metrics.CreateCounter("sky_eventbroker_bazaar_deliveries_total", "Bazaar fill processing outcomes", new CounterConfiguration { LabelNames = new[] { "result" } });
    private static readonly Counter Reclaimed = Metrics.CreateCounter("sky_eventbroker_bazaar_reclaimed_total", "Pending Bazaar fills reclaimed for retry");
    internal const string Stream = "bazaar:fills:v1";
    internal const string Group = "sky-eventbroker-bazaar";
    // A restart between separate ACK and DELETE calls would strand processed stream entries.
    internal const string CompleteScript = """
        redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
        return redis.call('XDEL', KEYS[1], ARGV[2])
        """;
    private readonly string consumer = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Bazaar fill consumer {Consumer} on {Stream}/{Group}", consumer, Stream, Group);
        var database = redis.GetDatabase();
        var pending = (RedisValue)"0-0";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                try { await database.StreamCreateConsumerGroupAsync(Stream, Group, "0-0", createStream: true); }
                catch (RedisServerException e) when (e.Message.StartsWith("BUSYGROUP")) { }
                var reclaimed = await database.StreamAutoClaimAsync(Stream, Group, consumer, 60_000, pending, 20);
                pending = reclaimed.NextStartId;
                if (reclaimed.ClaimedEntries.Length > 0)
                {
                    Reclaimed.Inc(reclaimed.ClaimedEntries.Length);
                    logger.LogInformation("Reclaimed {Count} pending Bazaar fills with consumer {Consumer}", reclaimed.ClaimedEntries.Length, consumer);
                }
                var entries = reclaimed.ClaimedEntries.Concat(await database.StreamReadGroupAsync(Stream, Group, consumer, ">", 20));
                foreach (var entry in entries)
                {
                    try { await Process(entry); }
                    catch (Exception e)
                    {
                        Deliveries.WithLabels("failure").Inc();
                        logger.LogError(e, "Could not process Bazaar fill {EntryId} on consumer {Consumer}; left pending for retry", entry.Id, consumer);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError(e, "Bazaar notification stream unavailable");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task Process(StreamEntry entry)
    {
        var payload = JObject.Parse(entry.Values.Single(v => v.Name == "message").Value!);
        var message = payload.ToObject<MessageContainer>();
        if (string.IsNullOrWhiteSpace(message?.User?.UserId) || message.SourceType != "bazaar" || message.SourceSubId != "filled")
            throw new InvalidOperationException("Invalid Bazaar fill event");
        ActivityContext.TryParse((string)payload["TraceParent"], (string)payload["TraceState"], true, out var parent);
        using var span = Traces.StartActivity("bazaar.fill.process", ActivityKind.Consumer, parent);
        span?.SetTag("bazaar.user_id", message.User.UserId);
        span?.SetTag("bazaar.order_id", (string)payload["OrderId"]);
        span?.SetTag("bazaar.reference", message.Reference);
        span?.SetTag("bazaar.revision", (long?)payload["Revision"]);
        span?.SetTag("messaging.message.id", entry.Id.ToString());
        var started = Stopwatch.GetTimestamp();
        logger.LogInformation("Processing Bazaar fill {EntryId}, reference {Reference}, order {OrderId}, user {UserId}, revision {Revision}; TraceId {TraceId}",
            entry.Id, message.Reference, (string)payload["OrderId"], message.User.UserId, (long?)payload["Revision"], Activity.Current?.TraceId.ToString());
        try
        {
            await Deliver(message);
            await redis.GetDatabase().ScriptEvaluateAsync(CompleteScript,
                new RedisKey[] { Stream }, new RedisValue[] { Group, entry.Id });
            Deliveries.WithLabels("success").Inc();
            logger.LogInformation("Completed Bazaar fill {EntryId}, reference {Reference}, user {UserId}; acknowledged in {ElapsedMs} ms; TraceId {TraceId}",
                entry.Id, message.Reference, message.User.UserId, Stopwatch.GetElapsedTime(started).TotalMilliseconds, Activity.Current?.TraceId.ToString());
        }
        catch (Exception e)
        {
            span?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            throw;
        }
    }

    protected virtual async Task Deliver(MessageContainer message)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MessageService>().AddMessage(message, retryFailures: true);
    }
}
