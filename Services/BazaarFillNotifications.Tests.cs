using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using StackExchange.Redis;

namespace Coflnet.Sky.EventBroker.Services;

public class BazaarFillNotificationsTests
{
    private class CaptureDelivery(IConnectionMultiplexer redis) : BazaarFillNotifications(redis, null, NullLogger<BazaarFillNotifications>.Instance)
    {
        public MessageContainer Delivered;
        public bool Fail;
        protected override Task Deliver(MessageContainer message)
        {
            if (Fail) throw new InvalidOperationException("Delivery unavailable");
            Delivered = message;
            return Task.CompletedTask;
        }
    }

    private static StreamEntry Entry => new("123-0", new[] { new NameValueEntry("message", JsonConvert.SerializeObject(new MessageContainer {
        User = new() { UserId = "1" }, SourceType = "bazaar", SourceSubId = "filled", Reference = "stable-order-reference",
        Message = "Your sell offer was filled!", Setings = new() { StoreIfOffline = true }
    })) });

    [Test]
    public async Task OfflineFillUsesExistingDeliveryTargetsBeforeAcknowledging()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var db = new Mock<IDatabase>();
        redis.Setup(r => r.GetDatabase(-1, null)).Returns(db.Object);
        var consumer = new CaptureDelivery(redis.Object);
        await consumer.Process(Entry);
        Assert.That(consumer.Delivered.User.UserId, Is.EqualTo("1"));
        Assert.That(consumer.Delivered.Setings.StoreIfOffline, Is.True);
        db.Verify(d => d.ScriptEvaluateAsync(BazaarFillNotifications.CompleteScript,
            It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == BazaarFillNotifications.Stream),
            It.Is<RedisValue[]>(values => values.Length == 2 && values[0] == BazaarFillNotifications.Group && values[1] == "123-0"),
            CommandFlags.None), Times.Once);
    }

    [Test]
    public void FailedDeliveryRemainsPendingForRetry()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var db = new Mock<IDatabase>();
        redis.Setup(r => r.GetDatabase(-1, null)).Returns(db.Object);
        var consumer = new CaptureDelivery(redis.Object) { Fail = true };
        Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Process(Entry));
        db.Verify(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None), Times.Never);
    }

    [Test]
    public async Task RedisPendingFillCanBeRecoveredByAnotherWorker()
    {
        var endpoint = Environment.GetEnvironmentVariable("BAZAAR_TEST_REDIS");
        if (string.IsNullOrEmpty(endpoint))
            Assert.Ignore("Set BAZAAR_TEST_REDIS to an isolated Redis instance for recovery integration testing.");
        using var redis = await ConnectionMultiplexer.ConnectAsync(endpoint);
        var db = redis.GetDatabase();
        try { await db.StreamCreateConsumerGroupAsync(BazaarFillNotifications.Stream, BazaarFillNotifications.Group, "0-0", createStream: true); }
        catch (RedisServerException e) when (e.Message.StartsWith("BUSYGROUP")) { }
        var id = await db.StreamAddAsync(BazaarFillNotifications.Stream, Entry.Values);
        await db.StreamReadGroupAsync(BazaarFillNotifications.Stream, BazaarFillNotifications.Group, "crashed-worker", ">");
        var pending = await db.StreamAutoClaimAsync(BazaarFillNotifications.Stream, BazaarFillNotifications.Group, "replacement-worker", 0, "0-0");
        var consumer = new CaptureDelivery(redis);
        foreach (var entry in pending.ClaimedEntries)
            await consumer.Process(entry);
        Assert.That((await db.StreamPendingAsync(BazaarFillNotifications.Stream, BazaarFillNotifications.Group)).PendingMessageCount, Is.Zero);
        Assert.That(await db.StreamRangeAsync(BazaarFillNotifications.Stream, id, id), Is.Empty);
    }
    [TestCase(null)]
    [TestCase("invalid")]
    [TestCase("00-11111111111111111111111111111111-2222222222222222-01")]
    public async Task TraceContextSurvivesDeliveryAndLegacyPayloadsStillWork(string traceParent)
    {
        Activity processed = null;
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == BazaarFillNotifications.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => processed = span
        };
        ActivitySource.AddActivityListener(listener);
        var payload = JObject.Parse(Entry.Values[0].Value);
        payload["TraceParent"] = traceParent;
        payload["TraceState"] = "vendor=value";
        payload["OrderId"] = "order-identity";
        payload["Revision"] = 42;
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(-1, null)).Returns(Mock.Of<IDatabase>());
        var consumer = new CaptureDelivery(redis.Object);
        await consumer.Process(new StreamEntry("456-0", new[] { new NameValueEntry("message", payload.ToString()) }));
        Assert.That(consumer.Delivered.Reference, Is.EqualTo("stable-order-reference"));
        Assert.That(processed.GetTagItem("bazaar.order_id"), Is.EqualTo("order-identity"));
        Assert.That(processed.GetTagItem("bazaar.revision"), Is.EqualTo(42));
        if (ActivityContext.TryParse(traceParent, "vendor=value", out var parent))
        {
            Assert.That(processed.TraceId, Is.EqualTo(parent.TraceId));
            Assert.That(processed.ParentSpanId, Is.EqualTo(parent.SpanId));
            Assert.That(processed.TraceStateString, Is.EqualTo("vendor=value"));
        }
    }

}
