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
using System.Runtime.Serialization;

namespace Coflnet.Sky.EventBroker.Services
{

    public class BaseBackgroundService : BackgroundService
    {
        private IServiceScopeFactory scopeFactory;
        private IConfiguration config;
        private ILogger<BaseBackgroundService> logger;
        private Prometheus.Counter consumeCount = Prometheus.Metrics.CreateCounter("sky_base_conume", "How many messages were consumed");

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
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<EventDbContext>();
            // make sure all migrations are applied
            await context.Database.MigrateAsync();

            var flipCons = Coflnet.Kafka.KafkaConsumer.Consume<TransactionEvent>(config["KAFKA_HOST"], config["TOPICS:TRANSACTIONS"], async lp =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = GetService(scope);
                await service.NewTransaction(lp);
            }, stoppingToken, "sky-referral", AutoOffsetReset.Earliest, new TransactionDeserializer());
            var verfify = Coflnet.Kafka.KafkaConsumer.Consume<VerificationEvent>(config["KAFKA_HOST"], config["TOPICS:VERIFIED"], async lp =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = GetService(scope);
                await service.Verified(lp.UserId, lp.MinecraftUuid);
            }, stoppingToken, "sky-referral");

            var cleanUp = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    using var scope = scopeFactory.CreateScope();
                    var service = GetService(scope);
                    await service.CleanDb();
                }
            });

            stoppingToken.Register(() =>
            {
                // force sending notifications
                Console.WriteLine("quiting");
            });

            await Task.WhenAny(flipCons, verfify);
            logger.LogError("One task exited");
            throw new Exception("a background task exited");
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
        }


        public class TransactionDeserializer : IDeserializer<Payments.Client.Model.TransactionEvent>
        {
            public Payments.Client.Model.TransactionEvent Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<Payments.Client.Model.TransactionEvent>(System.Text.Encoding.UTF8.GetString(data));
            }
        }

        private MessageService GetService(IServiceScope scope)
        {
            return scope.ServiceProvider.GetRequiredService<MessageService>();
        }
    }
}