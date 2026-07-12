using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.EventBroker.Controllers;
using Coflnet.Sky.Core;
using Coflnet.Payments.Client.Model;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using User = Coflnet.Sky.EventBroker.Models.User;
using Newtonsoft.Json;
using Coflnet.Sky.Commands.Shared;

namespace Coflnet.Sky.EventBroker.Services
{

    public class BaseBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<BaseBackgroundService> logger;
        private Prometheus.Counter cleanupCount = Prometheus.Metrics.CreateCounter("sky_eventbroker_cleanup", "How many events were cleaned up");

        public BaseBackgroundService(
            IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<BaseBackgroundService> logger)
        {
            this.scopeFactory = scopeFactory;
            this.config = config;
            this.logger = logger;
        }
        /// <summary>
        /// Called by asp.net on startup
        /// </summary>
        /// <param name="stoppingToken">is canceled when the applications stops</param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Migrating database");
            using (var scope = scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<EventDbContext>();
                // make sure all migrations are applied
                await context.Database.MigrateAsync();
            }
            logger.LogInformation("Starting consumer");
            var flipCons = Kafka.KafkaConsumer.Consume(config, config["TOPICS:TRANSACTIONS"], async lp =>
            {
                try
                {

                    using var scope = scopeFactory.CreateScope();
                    var service = GetService(scope);
                    await service.NewTransaction(lp);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while processing transaction");
                    throw;
                }
            }, stoppingToken, "sky-eventbroker", AutoOffsetReset.Earliest, new TransactionDeserializer());
            var verfify = Kafka.KafkaConsumer.ConsumeBatch<VerificationEvent>(config, config["TOPICS:VERIFIED"], async batch =>
            {
                try
                {
                    foreach (var lp in batch)
                    {
                        logger.LogInformation("Verification event received for {user}", lp.UserId);
                        using var scope = scopeFactory.CreateScope();
                        var service = GetService(scope);
                        await service.Verified(lp.UserId, lp.MinecraftUuid, lp.ExistingConCount);
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while processing verification");
                    throw;
                }
            }, stoppingToken, "sky-eventbroker", 2);
            var notification = Kafka.KafkaConsumer.ConsumeBatch(config, config["TOPICS:NOTIFICATIONS"], async lp =>
            {
                try
                {
                    await Parallel.ForEachAsync(lp, async (item, c) =>
                    {
                        await ProcessNotification(item);
                    });
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while processing notification");
                }
            }, stoppingToken, "sky-eventbroker", 3, AutoOffsetReset.Latest, new NotificationDeserializer());

            var lowballTopic = config["TOPICS:LOWBALL_OFFERS"];
            Task lowball = Task.CompletedTask;
            if (!string.IsNullOrEmpty(lowballTopic))
            {
                lowball = Kafka.KafkaConsumer.ConsumeBatch<LowballOfferMessage>(config, lowballTopic, async batch =>
                {
                    try
                    {
                        foreach (var offer in batch)
                        {
                            await ProcessLowballOffer(offer);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error while processing lowball offer");
                    }
                }, stoppingToken, "sky-eventbroker-lowball", 5, AutoOffsetReset.Latest);
            }

            var cleanUp = Task.Run(async () =>
            {
                logger.LogInformation("Starting cleanup task");
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var service = GetService(scope);
                        var count = await service.CleanDb();
                        cleanupCount.Inc(count);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error while cleaning db");
                        throw;
                    }
                }
                logger.LogInformation("Stopping cleanup task");
            });

            stoppingToken.Register(() =>
            {
                // force sending notifications
                Console.WriteLine("quiting");
            });

            await Task.WhenAny(flipCons, verfify, cleanUp, notification, lowball);
            logger.LogError("One task exited");
            await notification;
            logger.LogError("Notification task exited");

            throw new Exception("a background task exited");
        }

        private async Task ProcessNotification(FirebaseNotification notification)
        {
            if (!(notification.data?.TryGetValue("userId", out var userId) ?? false))
            {
                logger.LogError("Notification event received without userId, {notification}", JsonConvert.SerializeObject(notification));
                return;
            }
            using var scope = scopeFactory.CreateScope();
            var service = GetService(scope);
            string extraSubId = "";
            if (notification.data.TryGetValue("whitelist", out var whitelistData))
            {
                var whitelist = JsonConvert.DeserializeObject<ListEntry>(whitelistData);
                if (whitelist.Tags != null && whitelist.Tags.Count > 0)
                    extraSubId = ";" + string.Join(',', whitelist.Tags);
            }
            await service.AddMessage(new MessageContainer()
            {
                Message = notification.body,
                SourceSubId = notification.data["subId"] + extraSubId,
                ImageLink = notification.icon,
                Data = notification.data,
                SourceType = SourceType.Subscription.ToString(),
                Link = notification.click_action,
                Summary = notification.title,
                Reference = (notification.title + notification.click_action.Split("/").Last()).Truncate(32),
                User = new User()
                {
                    UserId = userId
                }
            });
            logger.LogInformation("Notification event received for {user}", userId);
        }

        private async Task ProcessLowballOffer(LowballOfferMessage offer)
        {
            using var scope = scopeFactory.CreateScope();
            var service = GetService(scope);
            var db = scope.ServiceProvider.GetRequiredService<EventDbContext>();
            var sourceType = SourceType.Lowball.ToString();

            var subs = await db.Subscriptions
                .Where(s => s.SourceType == sourceType || s.SourceType == "*" || s.SourceType == "Any")
                .Include(s => s.Targets).ThenInclude(t => t.Target)
                .ToListAsync();

            var itemLink = $"https://sky.coflnet.com/item/{offer.ItemTag}";
            var cleanName = offer.ItemName?.Replace("§", "").TrimStart() ?? offer.ItemTag;
            var priceText = offer.AskingPrice.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

            var notifiedUsers = new HashSet<string>();
            foreach (var sub in subs)
            {
                if (notifiedUsers.Contains(sub.UserId))
                    continue;
                notifiedUsers.Add(sub.UserId);

                await service.AddMessage(new MessageContainer()
                {
                    Message = $"New lowball offer: {cleanName} for {priceText} coins",
                    Summary = $"Lowball: {cleanName}",
                    SourceType = sourceType,
                    SourceSubId = offer.ItemTag,
                    Link = itemLink,
                    ImageLink = $"https://sky.coflnet.com/static/icon/{offer.ItemTag}",
                    Reference = ("lb" + offer.OfferId.ToString()[..8]).Truncate(32),
                    User = new User() { UserId = sub.UserId }
                });
            }

            logger.LogInformation("Lowball offer {OfferId} for {ItemTag} notified {Count} users", offer.OfferId, offer.ItemTag, notifiedUsers.Count);
        }

        [DataContract]
        public class VerificationEvent
        {
            /// <summary>
            /// UserId of the user
            /// </summary>
            /// <value></value>
            [DataMember(Name = "userId")]
            public string UserId { get; set; }
            /// <summary>
            /// Minecraft uuid of the verified account
            /// </summary>
            /// <value></value>
            [DataMember(Name = "uuid")]
            public string MinecraftUuid { get; set; }
            /// <summary>
            /// How many existing verifications are on this minecraft account
            /// </summary>
            [DataMember(Name = "existing")]
            public int ExistingConCount { get; set; }
        }


        public class TransactionDeserializer : IDeserializer<TransactionEvent>
        {
            public TransactionEvent Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<TransactionEvent>(System.Text.Encoding.UTF8.GetString(data));
            }
        }
        public class NotificationDeserializer : IDeserializer<FirebaseNotification>
        {
            public FirebaseNotification Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<FirebaseNotification>(System.Text.Encoding.UTF8.GetString(data));
            }
        }

        private MessageService GetService(IServiceScope scope)
        {
            return scope.ServiceProvider.GetRequiredService<MessageService>();
        }
    }

    [DataContract]
    public class LowballOfferMessage
    {
        [DataMember(Name = "offerId")]
        public Guid OfferId { get; set; }
        [DataMember(Name = "userId")]
        public string UserId { get; set; }
        [DataMember(Name = "itemTag")]
        public string ItemTag { get; set; }
        [DataMember(Name = "itemName")]
        public string ItemName { get; set; }
        [DataMember(Name = "askingPrice")]
        public long AskingPrice { get; set; }
        [DataMember(Name = "minecraftAccount")]
        public Guid MinecraftAccount { get; set; }
        [DataMember(Name = "createdAt")]
        public DateTimeOffset CreatedAt { get; set; }
        [DataMember(Name = "apiAuctionJson")]
        public string ApiAuctionJson { get; set; }
        [DataMember(Name = "itemCount")]
        public int ItemCount { get; set; }
    }
}