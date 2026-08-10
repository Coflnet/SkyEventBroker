using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.EventBroker.Models;
using Coflnet.Sky.Indexer.Client.Api;
using Coflnet.Sky.Indexer.Client.Model;
using Coflnet.Sky.Settings.Client.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class MessageServiceTests
{
    [Test]
    public async Task NewPayment_NoEmailAnywhere_DoesNotThrowAndDropsSilently()
    {
        // Regression test for the poison-loop bug: NewPayment used to throw
        // InvalidOperationException when no email could be found anywhere. That exception
        // propagates into Kafka.KafkaConsumer.Consume, which re-enters the consume loop with no
        // delay and no offset commit, permanently blocking the partition. It must instead log and
        // drop the confirmation.
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>().UseSqlite(connection).Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var userApi = new Mock<IUserApi>();
        userApi.Setup(api => api.UserEmailGetAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GoogleUser)null);
        var service = CreateService(db, userApi.Object);

        var payment = new PaymentEvent
        {
            UserId = "42",
            PaymentProvider = "coingate",
            PaymentProviderTransactionId = "pi_no_email"
        };

        Assert.DoesNotThrowAsync(() => service.NewPayment(payment));
        Assert.That(await db.PurchaseConfirmationDeliveries.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task NewPayment_PrefersEventEmailOverIndexerLookup()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>().UseSqlite(connection).Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var userApi = new Mock<IUserApi>();
        var service = CreateService(db, userApi.Object);

        var payment = new PaymentEvent
        {
            UserId = "42",
            Email = "buyer@example.test",
            LegalLocale = "de",
            PaymentProvider = "stripe",
            PaymentProviderTransactionId = "pi_has_email"
        };

        await service.NewPayment(payment);

        userApi.Verify(
            api => api.UserEmailGetAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var delivery = await db.PurchaseConfirmationDeliveries.SingleAsync();
        Assert.That(delivery.Recipient, Is.EqualTo("buyer@example.test"));
        Assert.That(delivery.Locale, Is.EqualTo("de"));
    }

    [Test]
    public async Task NewPayment_FallsBackToIndexerWhenEventEmailIsBlank()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>().UseSqlite(connection).Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var userApi = new Mock<IUserApi>();
        userApi.Setup(api => api.UserEmailGetAsync("42", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser { Email = "indexer@example.test" });
        var service = CreateService(db, userApi.Object);

        var payment = new PaymentEvent
        {
            UserId = "42",
            PaymentProvider = "lemonsqueezy",
            PaymentProviderTransactionId = "pi_blank_email"
        };

        await service.NewPayment(payment);

        userApi.Verify(
            api => api.UserEmailGetAsync("42", It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var delivery = await db.PurchaseConfirmationDeliveries.SingleAsync();
        Assert.That(delivery.Recipient, Is.EqualTo("indexer@example.test"));
    }

    [Test]
    public async Task NewPayment_AcceptsNonNumericUserId()
    {
        // Regression test: UserId is Payments' opaque User.ExternalId with no numeric guarantee.
        // The old `!int.TryParse(payment.UserId, out _)` guard silently discarded every
        // confirmation for a non-numeric id.
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>().UseSqlite(connection).Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var userApi = new Mock<IUserApi>();
        var service = CreateService(db, userApi.Object);

        var payment = new PaymentEvent
        {
            UserId = "ordinary-user",
            Email = "buyer@example.test",
            PaymentProvider = "stripe",
            PaymentProviderTransactionId = "pi_non_numeric"
        };

        await service.NewPayment(payment);

        Assert.That(await db.PurchaseConfirmationDeliveries.CountAsync(), Is.EqualTo(1));
    }

    private static MessageService CreateService(EventDbContext db, IUserApi userApi)
    {
        // GetCurrentValue<AccountInfo> only needs a working ISettingsApi that resolves quickly;
        // NoContent makes it return the default (null) without any retry delay.
        var settingsApi = new Mock<ISettingsApi>();
        settingsApi.Setup(api => api.GetSettingWithHttpInfoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Coflnet.Sky.Settings.Client.Client.ApiResponse<string>(
                HttpStatusCode.NoContent, (string)null));
        var settingsService = new SettingsService(
            new ConfigurationBuilder().Build(),
            NullLogger<SettingsService>.Instance,
            settingsApi.Object);

        // NewPayment only touches db, userApi, settingsService and the logger; the remaining
        // dependencies are safe to leave null for these tests.
        return new MessageService(
            db,
            connection: null,
            productsApi: null,
            logger: NullLogger<MessageService>.Instance,
            lockService: null,
            settingsService: settingsService,
            config: null,
            premiumService: null,
            doubleNotificationPreventer: null,
            pushService: null,
            userApi: userApi);
    }
}
