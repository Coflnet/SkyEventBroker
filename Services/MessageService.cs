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
using Coflnet.Sky.Commands.Shared;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.EventBroker.Services
{
    public class MessageService
    {
        private EventDbContext db;
        private ConnectionMultiplexer connection;
        private Payments.Client.Api.ProductsApi productsApi;
        private ILogger<MessageService> Logger;
        private AsyncUserLockService lockService;
        private SettingsService settingsService;
        private IConfiguration config;
        private PremiumService premiumService;

        public MessageService(EventDbContext db, ConnectionMultiplexer connection, Payments.Client.Api.ProductsApi productsApi,
                        ILogger<MessageService> logger, AsyncUserLockService lockService, SettingsService settingsService, IConfiguration config, PremiumService premiumService)
        {
            this.db = db;
            this.connection = connection;
            this.productsApi = productsApi;
            Logger = logger;
            this.lockService = lockService;
            this.settingsService = settingsService;
            this.config = config;
            this.premiumService = premiumService;
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
            var received = await pubsub.PublishAsync("uev" + message.User.UserId, serialized);
            Logger.LogInformation("published for {user} source {source} count {count}", message.User.UserId, message.SourceType, received);
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
            db.Confirms.Add(new ReceiveConfirm() { Reference = refence });
            return db.SaveChangesAsync();
        }

        internal async Task NewTransaction(TransactionEvent lp)
        {
            if (lp.UserId == null)
                return;
            var message = $"Your topup of {FormatCoins(lp.Amount)} CoflCoins was received";
            if (lp.Amount < 1800)
                message = $"You received {FormatCoins(lp.Amount)} CoflCoins";
            if (lp.ProductSlug == "transfer")
                message = $"You received {FormatCoins(lp.Amount)} CoflCoins from someone";
            if (lp.ProductSlug == "compensation")
                if (lp.Amount < 0)
                    message = $"{FormatCoins(lp.Amount)} CoflCoins were deducted from your account for {lp.Reference}";
                else
                    message = $"You received {FormatCoins(lp.Amount)} CoflCoins as compensation for {lp.Reference}";
            if (lp.ProductSlug == config["PRODUCTS:VERIFY_MC"])
                return;
            if (lp.ProductSlug == config["PRODUCTS:REFERRAL_BONUS"])
                message = $"You received {FormatCoins(lp.Amount)} CoflCoins from the referral system";
            var sourceType = "topup";
            if (lp.ProductSlug == config["PRODUCTS:TEST_PREMIUM"])
            {
                var product = await productsApi.ProductsPProductSlugGetAsync(lp.ProductSlug);
                var timeInDays = TimeSpan.FromSeconds(product.OwnershipSeconds).TotalDays;
                message = $"You received {timeInDays} days of test premium for verifying your minecraft account";
            }
            else if (lp.Amount < 0)
            {
                var product = await productsApi.ProductsPProductSlugGetAsync(lp.ProductSlug);
                message = $"You purchased {product?.Title ?? lp.ProductSlug}";
                sourceType = "purchase";
            }

            if (lp.Timestamp > DateTime.UtcNow - TimeSpan.FromHours(1))
                await AddMessage(new MessageContainer()
                {
                    Data = lp,
                    Message = message,
                    Reference = "transaction" + lp.Id,
                    SourceType = sourceType,
                    Setings = new Models.Settings() { ConfirmDelivery = true, PlaySound = true },
                    User = new Models.User()
                    {
                        UserId = lp.UserId
                    }
                });

            await lockService.GetLock(lp.UserId, async (u) =>
            {
                Logger.LogInformation("handling transaction for {user}", lp.UserId);
                var current = await settingsService.GetCurrentValue<AccountInfo>(u, "accountInfo", default);

                if (current == null)
                {
                    Logger.LogInformation($"No account info found for {u}");
                    return;
                }
                if (config["PRODUCTS:PREMIUM"] == lp.ProductSlug || config["PRODUCTS:TEST_PREMIUM"] == lp.ProductSlug)
                {
                    Logger.LogInformation("changing premium time for {user}", lp.UserId);

                    var when = await premiumService.ExpiresWhen(lp.UserId);
                    if (when > DateTime.Now)
                    {
                        current.ExpiresAt = when;
                        current.Tier = AccountTier.PREMIUM;
                        await settingsService.UpdateSetting(u, "accountInfo", current);
                    }
                }
            });
        }

        private static string FormatCoins(double amount)
        {
            return string.Format("{0:n0}", Convert.ToInt32(amount));
        }

        internal async Task<IEnumerable<MessageContainer>> GetMessages(string userId)
        {
            return await db.Messages.Where(m => m.User.UserId == userId).Include(m => m.Setings).ToListAsync();
        }

        internal async Task<int> CleanDb()
        {
            var minTime = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            var oldestTime = DateTime.UtcNow - TimeSpan.FromDays(30);
            var old = await db.Messages.Where(m => m.Timestamp < minTime && !m.Setings.StoreIfOffline || m.Timestamp < oldestTime).Include(m => m.Setings).Include(m => m.User).ToListAsync();
            db.RemoveRange(old.Select(o => o.Setings).Where(s => s != null));
            db.RemoveRange(old.Select(o => o.User).Where(u => u != null));
            db.Messages.RemoveRange(old);
            var remCount = await db.SaveChangesAsync();
            Logger.LogInformation("Removed {remCount} message from db", remCount);
            return remCount;
        }

        internal async Task Verified(string userId, string minecraftUuid)
        {
            await AddMessage(new MessageContainer()
            {
                Data = minecraftUuid,
                Message = "You successfully verified your minecraft account",
                Reference = minecraftUuid,
                SourceType = "mcVerify",
                Setings = new Models.Settings() { ConfirmDelivery = true, PlaySound = true },
                User = new Models.User()
                {
                    UserId = userId
                }
            });

            await lockService.GetLock(userId, async (u) =>
            {
                var current = await settingsService.GetCurrentValue<AccountInfo>(u, "accountInfo", () => null);
                if (current == null)
                {
                    Logger.LogInformation($"No account info found for {userId}");
                    return;
                }
                current.McIds.Add(minecraftUuid);
                await settingsService.UpdateSetting(u, "accountInfo", current);
            });
        }
    }
}
