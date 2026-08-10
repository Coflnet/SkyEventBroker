using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Services;

public class PurchaseConfirmationDeliveryServiceTests
{
    [Test]
    public async Task ProcessOne_RetriesFailureThenKeepsSingleSentReceipt()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var payment = new PaymentEvent
        {
            UserId = "42",
            ProductId = "7",
            PaymentProvider = "stripe",
            PaymentProviderTransactionId = "pi_123",
            Timestamp = DateTime.UtcNow
        };
        db.PurchaseConfirmationDeliveries.Add(
            new PurchaseConfirmationDelivery
            {
                Reference = "mail0123456789ABCDEF01234567",
                Recipient = "buyer@example.test",
                Locale = "en-US",
                Payload = JsonConvert.SerializeObject(payment),
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var sender = new RecordingSender { FailNext = true };
        var service = new PurchaseConfirmationDeliveryService(
            db,
            sender,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        Assert.That(await service.ProcessOneAsync(), Is.True);
        db.ChangeTracker.Clear();
        var failed = await db.PurchaseConfirmationDeliveries.SingleAsync();
        Assert.That(failed.SentAt, Is.Null);
        Assert.That(failed.Attempts, Is.EqualTo(1));
        Assert.That(failed.LastError, Is.Not.Empty);

        failed.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.That(await service.ProcessOneAsync(), Is.True);
        Assert.That(await service.ProcessOneAsync(), Is.False);

        db.ChangeTracker.Clear();
        var sent = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .SingleAsync();
        Assert.That(sender.Deliveries, Has.Count.EqualTo(1));
        Assert.That(sent.SentAt, Is.Not.Null);
        Assert.That(sent.Attempts, Is.EqualTo(2));
        Assert.That(sent.Recipient, Is.Null);
        Assert.That(sent.Payload, Is.Null);
    }

    [Test]
    public async Task ProcessOne_SlowFailureSchedulesRetryStrictlyInTheFuture()
    {
        // Regression test: retryAt used to be computed from "now" captured before the send, so a
        // slow failure (SmtpClient's ~100s default timeout) could push retryAt into the past,
        // making the backoff a no-op.
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var payment = new PaymentEvent
        {
            UserId = "42",
            ProductId = "7",
            PaymentProvider = "stripe",
            PaymentProviderTransactionId = "pi_slow",
            Timestamp = DateTime.UtcNow
        };
        db.PurchaseConfirmationDeliveries.Add(
            new PurchaseConfirmationDelivery
            {
                Reference = "mailSLOW00000000000000000001",
                Recipient = "buyer@example.test",
                Locale = "en-US",
                Payload = JsonConvert.SerializeObject(payment),
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var sender = new RecordingSender
        {
            FailNext = true,
            DelayBeforeFail = TimeSpan.FromMilliseconds(50)
        };
        var service = new PurchaseConfirmationDeliveryService(
            db,
            sender,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        Assert.That(await service.ProcessOneAsync(), Is.True);

        db.ChangeTracker.Clear();
        var failed = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(failed.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public async Task ProcessOne_RowIsParkedAfterMaxAttemptsAndNotReturnedAgain()
    {
        // Regression test: a permanently failing row (bad address, mismatched terms hash, ...)
        // used to retry forever; it must be parked (FailedAt set) after the max attempt count and
        // then be excluded from the claim query.
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EventDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var payment = new PaymentEvent
        {
            UserId = "42",
            ProductId = "7",
            PaymentProvider = "stripe",
            PaymentProviderTransactionId = "pi_parked",
            Timestamp = DateTime.UtcNow
        };
        db.PurchaseConfirmationDeliveries.Add(
            new PurchaseConfirmationDelivery
            {
                Reference = "mailPARKED0000000000000000001",
                Recipient = "buyer@example.test",
                Locale = "en-US",
                Payload = JsonConvert.SerializeObject(payment),
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var sender = new RecordingSender { AlwaysFail = true };
        var service = new PurchaseConfirmationDeliveryService(
            db,
            sender,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.That(await service.ProcessOneAsync(), Is.True);
            db.ChangeTracker.Clear();
            var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
            if (row.FailedAt != null)
                break;
            // skip the real backoff wait so the next iteration's claim query picks it up again
            row.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            db.Update(row);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        var parked = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(parked.Attempts, Is.EqualTo(10));
        Assert.That(parked.FailedAt, Is.Not.Null);
        Assert.That(parked.SentAt, Is.Null);

        Assert.That(await service.ProcessOneAsync(), Is.False);
    }

    [Test]
    public void PurchaseReference_NormalizesProviderAndType()
    {
        Assert.That(
            MessageService.PurchaseConfirmationReference(
                " Stripe ",
                " pi_123 ",
                " TRIAL "),
            Is.EqualTo(
                MessageService.PurchaseConfirmationReference(
                    "stripe",
                    "pi_123",
                    "trial")));
    }

    private sealed class RecordingSender : IPurchaseConfirmationEmailSender
    {
        public bool FailNext { get; set; }
        public bool AlwaysFail { get; set; }
        public TimeSpan? DelayBeforeFail { get; set; }
        public List<string> Deliveries { get; } = [];

        public async Task SendAsync(
            string toEmail,
            string locale,
            PaymentEvent payment,
            string confirmationReference = null)
        {
            if (DelayBeforeFail.HasValue)
                await Task.Delay(DelayBeforeFail.Value);
            if (AlwaysFail || FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("temporary SMTP failure");
            }

            Deliveries.Add(confirmationReference);
        }
    }
}
