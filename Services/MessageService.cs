using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Newtonsoft.Json;
using Coflnet.Payments.Client.Model;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.EventBroker.Services
{
    public class MessageService
    {
        private EventDbContext db;
        private ConnectionMultiplexer connection;
        private Payments.Client.Api.ProductsApi productsApi;
        private ILogger<MessageService> Logger;

        public MessageService(EventDbContext db, ConnectionMultiplexer connection, Payments.Client.Api.ProductsApi productsApi, ILogger<MessageService> logger)
        {
            this.db = db;
            this.connection = connection;
            this.productsApi = productsApi;
            Logger = logger;
        }

        public async Task<MessageContainer> AddMessage(MessageContainer message)
        {
            if (message.Timestamp == default)
            {
                message.Timestamp = DateTime.Now;
            }
            if (string.IsNullOrEmpty(message.Reference))
                message.Reference = Guid.NewGuid().ToString().Replace("-", "");
            var pubsub = connection.GetSubscriber();
            var serialized = JsonConvert.SerializeObject(message);
            Console.WriteLine("sending " + serialized);
            var received = await pubsub.PublishAsync("u" + message.User.UserId, serialized);
            // message has been received by someone and can be dropped
            if (received > 0)
                return message;

            // not sure if someone received the message, store it
            db.Messages.Add(message);
            await db.SaveChangesAsync();

            return message;
        }

        internal Task Received(string refence)
        {
            db.Confirms.Add(new ReceiveConfirm(){Reference = refence});
            return db.SaveChangesAsync();
        }

        internal async Task NewTransaction(TransactionEvent lp)
        {

            var message = $"Your topup of {lp.Amount} was received";
            var sourceType = "topup";
            if (lp.Amount > 0)
            {
                var product = await productsApi.ProductsPProductSlugGetAsync(lp.ProductSlug);
                message = $"You purchased {product.Title}";
                sourceType = "purchase";
            }

            await AddMessage(new MessageContainer()
            {
                Data = lp,
                Message = message,
                Reference = "transaction" + lp.Id,
                SourceType = sourceType,
                Setings = new Settings() { ConfirmDelivery = true, PlaySound = true },
                User = new Models.User()
                {
                    UserId = lp.UserId
                }
            });
        }

        internal async Task<IEnumerable<MessageContainer>> GetMessages(string userId)
        {
            return await db.Messages.Where(m=>m.User.UserId == userId).Include(m=>m.Setings).ToListAsync();
        }

        internal async Task CleanDb()
        {
            var minTime = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            var oldestTime = DateTime.UtcNow - TimeSpan.FromDays(30);
            var old = await db.Messages.Where(m=>m.Timestamp < minTime && !m.Setings.StoreIfOffline || m.Timestamp < oldestTime).Include(m=>m.Setings).Include(m=>m.User).ToListAsync();
            db.RemoveRange(old.Select(o=>o.Setings).Where(s=>s != null));
            db.RemoveRange(old.Select(o=>o.User).Where(u=>u != null));
            db.Messages.RemoveRange(old);
            var remCount = await db.SaveChangesAsync();
            Logger.LogInformation("Removed {remCount} message from db", remCount);
        }

        internal async Task Verified(string userId, string minecraftUuid)
        {
            await AddMessage(new MessageContainer()
            {
                Data = minecraftUuid,
                Message = "You successfully verified your minecraft account",
                Reference = minecraftUuid,
                SourceType = "mcVerify",
                Setings = new Settings() { ConfirmDelivery = true, PlaySound = true },
                User = new Models.User()
                {
                    UserId = userId
                }
            });
        }
    }
}
