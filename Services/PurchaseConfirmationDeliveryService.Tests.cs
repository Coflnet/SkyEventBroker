using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Coflnet.Sky.Indexer.Client.Api;
using Coflnet.Sky.Indexer.Client.Client;
using Coflnet.Sky.Indexer.Client.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
            Mock.Of<IUserApi>(),
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
            Mock.Of<IUserApi>(),
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        Assert.That(await service.ProcessOneAsync(), Is.True);

        db.ChangeTracker.Clear();
        var failed = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(failed.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public async Task ProcessOne_TransientRowIsNotParkedAfterFastAttemptsButWaitsForSlowRetry()
    {
        // A transiently failing row used to be parked after 10 attempts (~13.5 minutes), which
        // fired the critical alert for any short SMTP outage. It now moves to a 30 minute retry.
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
            Mock.Of<IUserApi>(),
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.That(await service.ProcessOneAsync(), Is.True);
            db.ChangeTracker.Clear();
            var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
            if (row.FailedAt != null || attempt == 9)
                break;
            // skip the real backoff wait so the next iteration's claim query picks it up again
            row.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            db.Update(row);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        var waiting = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(waiting.Attempts, Is.EqualTo(10));
        Assert.That(waiting.FailedAt, Is.Null);
        Assert.That(waiting.SentAt, Is.Null);
        Assert.That(waiting.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(29)));

        Assert.That(await service.ProcessOneAsync(), Is.False);
    }

    [Test]
    public async Task ProcessOne_RowHasNoRecipient_ResolvesViaIndexerAndSendsToIt()
    {
        // NewPayment now enqueues rows with a null Recipient whenever the indexer lookup could
        // not be resolved inline, so delivery is never gated on the email address at enqueue
        // time. This is that address being resolved later, once the indexer actually has it.
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
            PaymentProviderTransactionId = "pi_resolve_later",
            Timestamp = DateTime.UtcNow
        };
        db.PurchaseConfirmationDeliveries.Add(
            new PurchaseConfirmationDelivery
            {
                Reference = "mailRESOLVE000000000000000001",
                Recipient = null,
                Locale = "en-US",
                Payload = JsonConvert.SerializeObject(payment),
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var userApi = new Mock<IUserApi>();
        userApi.Setup(api => api.UserEmailGetAsync("42", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser { Email = "resolved@example.test" });
        var sender = new RecordingSender();
        var service = new PurchaseConfirmationDeliveryService(
            db,
            sender,
            userApi.Object,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        Assert.That(await service.ProcessOneAsync(), Is.True);

        Assert.That(sender.Recipients, Is.EqualTo(new[] { "resolved@example.test" }));
        db.ChangeTracker.Clear();
        var sent = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(sent.SentAt, Is.Not.Null);
    }

    [Test]
    public async Task ProcessOne_RowHasNoRecipientAndIndexerStillHasNone_SchedulesRetryInsteadOfSendingOrParking()
    {
        // Mirrors the production failure: the indexer throws ApiException("user_not_found") for
        // an account with no email on file. Nothing must be sent, and this is a transient/data
        // condition like any other send failure - it goes through the normal backoff, not an
        // immediate park.
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
            PaymentProviderTransactionId = "pi_still_no_email",
            Timestamp = DateTime.UtcNow
        };
        db.PurchaseConfirmationDeliveries.Add(
            new PurchaseConfirmationDelivery
            {
                Reference = "mailSTILLNONE00000000000001",
                Recipient = null,
                Locale = "en-US",
                Payload = JsonConvert.SerializeObject(payment),
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var userApi = new Mock<IUserApi>();
        userApi.Setup(api => api.UserEmailGetAsync("42", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException(404, "Error calling UserEmailGet: User not found (, user_not_found)"));
        var sender = new RecordingSender();
        var service = new PurchaseConfirmationDeliveryService(
            db,
            sender,
            userApi.Object,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        Assert.That(await service.ProcessOneAsync(), Is.True);

        Assert.That(sender.Recipients, Is.Empty);
        db.ChangeTracker.Clear();
        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.SentAt, Is.Null);
        Assert.That(row.FailedAt, Is.Null);
        Assert.That(row.Attempts, Is.EqualTo(1));
        Assert.That(row.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow));
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

    private static async Task<(SqliteConnection Connection, EventDbContext Db)> CreateDb()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var db = new EventDbContext(new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    private static async Task AddRow(
        EventDbContext db,
        string reference,
        DateTime? createdAt = null,
        int attempts = 0)
    {
        db.PurchaseConfirmationDeliveries.Add(new PurchaseConfirmationDelivery
        {
            Reference = reference,
            Recipient = "buyer@example.test",
            Locale = "en-US",
            Payload = JsonConvert.SerializeObject(new PaymentEvent
            {
                UserId = "42",
                PaymentProvider = "stripe",
                PaymentProviderTransactionId = "pi_x",
                Timestamp = DateTime.UtcNow
            }),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow.AddSeconds(-1),
            Attempts = attempts
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static PurchaseConfirmationDeliveryService CreateService(
        EventDbContext db,
        IPurchaseConfirmationEmailSender sender) => new(
            db,
            sender,
            Mock.Of<IUserApi>(),
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

    private static double Counter(Prometheus.Counter counter, string reason) =>
        counter.WithLabels(reason).Value;

    private static IEnumerable<TestCaseData> ClassifierCases()
    {
        var permanent = PurchaseConfirmationFailureKind.Permanent;
        var transient = PurchaseConfirmationFailureKind.Transient;
        yield return new(new PurchaseConfirmationException("agreement_mismatch", "x"), "agreement_mismatch", permanent);
        yield return new(new PurchaseConfirmationException("terms_mismatch", "x"), "terms_mismatch", permanent);
        yield return new(new PurchaseConfirmationException("withdrawal_mismatch", "x"), "withdrawal_mismatch", permanent);
        yield return new(new PurchaseConfirmationException("documents_not_loaded", "x"), "documents_not_loaded", transient);
        yield return new(new PurchaseConfirmationException("no_recipient", "x"), "no_recipient", transient);
        yield return new(new PurchaseConfirmationException("recipient_lookup_failed", "x", new TimeoutException()), "recipient_lookup_failed", transient);
        yield return new(new PurchaseConfirmationException("smtp_not_configured", "x"), "smtp_not_configured", transient);
        yield return new(new SmtpFailedRecipientException(SmtpStatusCode.MailboxUnavailable, "a@b.test"), "smtp_rejected_recipient", permanent);
        yield return new(new SmtpFailedRecipientException(SmtpStatusCode.MailboxBusy, "a@b.test"), "smtp_error", transient);
        yield return new(new SmtpException(SmtpStatusCode.MailboxUnavailable), "smtp_rejected_recipient", permanent);
        yield return new(new SmtpException(SmtpStatusCode.GeneralFailure), "smtp_error", transient);
        yield return new(new SmtpException(SmtpStatusCode.ServiceNotAvailable), "smtp_error", transient);
        yield return new(new SocketException(), "smtp_error", transient);
        yield return new(new TaskCanceledException(), "smtp_error", transient);
        yield return new(new HttpRequestException("indexer"), "recipient_lookup_failed", transient);
        yield return new(new ApiException(500, "boom"), "recipient_lookup_failed", transient);
        yield return new(new ApiException(404, "user_not_found"), "no_recipient", transient);
        yield return new(new DbUpdateException("db"), "database", transient);
        yield return new(new TestDbException(), "database", transient);
        yield return new(new InvalidOperationException("whatever"), "other", transient);
        yield return new(new InvalidOperationException("outer", new SmtpFailedRecipientException(SmtpStatusCode.MailboxUnavailable, "a@b.test")), "smtp_rejected_recipient", permanent);
    }

    [TestCaseSource(nameof(ClassifierCases))]
    public void Classify_MapsExceptionToReasonAndKind(
        Exception exception,
        string reason,
        PurchaseConfirmationFailureKind kind)
    {
        Assert.That(
            PurchaseConfirmationFailure.Classify(exception),
            Is.EqualTo((reason, kind)));
        Assert.That(PurchaseConfirmationFailure.Reasons, Does.Contain(reason));
    }

    [Test]
    public void FormatError_RoundTripsReason()
    {
        var error = PurchaseConfirmationFailure.FormatError("smtp_error", new SmtpException("down"));
        Assert.That(error, Is.EqualTo("[smtp_error] SmtpException: down"));
        Assert.That(PurchaseConfirmationFailure.ParseReason(error), Is.EqualTo("smtp_error"));
        Assert.That(PurchaseConfirmationFailure.ParseReason("InvalidOperationException: old"), Is.Null);
        Assert.That(PurchaseConfirmationFailure.ParseReason("[unknown] x"), Is.Null);
    }

    [Test]
    public async Task ProcessOne_PermanentFailureParksOnFirstAttemptWithReason()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(db, "mailPERMANENT00000000000000001");
        var before = Counter(PurchaseConfirmationMetrics.Parked, "terms_mismatch");
        var failedBefore = Counter(PurchaseConfirmationMetrics.AttemptFailed, "terms_mismatch");
        var sender = new RecordingSender
        {
            Failure = new PurchaseConfirmationException("terms_mismatch", "hash differs")
        };

        Assert.That(await CreateService(db, sender).ProcessOneAsync(), Is.True);

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.FailedAt, Is.Not.Null);
        Assert.That(row.Attempts, Is.EqualTo(1));
        Assert.That(row.LastError, Is.EqualTo(
            "[terms_mismatch] PurchaseConfirmationException: hash differs"));
        Assert.That(Counter(PurchaseConfirmationMetrics.Parked, "terms_mismatch"), Is.EqualTo(before + 1));
        Assert.That(Counter(PurchaseConfirmationMetrics.AttemptFailed, "terms_mismatch"), Is.EqualTo(failedBefore + 1));
    }

    [Test]
    public async Task ProcessOne_TransientFailureAfterFastAttemptsRetriesEvery30MinutesInsteadOfParking()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(db, "mailSLOWTAIL0000000000000000001", attempts: 9);
        var parkedBefore = Counter(PurchaseConfirmationMetrics.Parked, "smtp_error");
        var failedBefore = Counter(PurchaseConfirmationMetrics.AttemptFailed, "smtp_error");
        var sender = new RecordingSender { Failure = new SmtpException("connection refused") };

        Assert.That(await CreateService(db, sender).ProcessOneAsync(), Is.True);

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.Attempts, Is.EqualTo(10));
        Assert.That(row.FailedAt, Is.Null);
        Assert.That(row.NextAttemptAt, Is.EqualTo(DateTime.UtcNow.AddMinutes(30)).Within(TimeSpan.FromSeconds(30)));
        Assert.That(row.LastError, Does.StartWith("[smtp_error] "));
        Assert.That(Counter(PurchaseConfirmationMetrics.Parked, "smtp_error"), Is.EqualTo(parkedBefore));
        Assert.That(Counter(PurchaseConfirmationMetrics.AttemptFailed, "smtp_error"), Is.EqualTo(failedBefore + 1));
    }

    [Test]
    public async Task ProcessOne_FastAttemptsKeepShortBackoff()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(db, "mailFASTBACKOFF0000000000000001", attempts: 2);
        var sender = new RecordingSender { Failure = new SmtpException("down") };

        await CreateService(db, sender).ProcessOneAsync();

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.NextAttemptAt, Is.LessThan(DateTime.UtcNow.AddMinutes(6)));
    }

    [Test]
    public async Task ProcessOne_TransientFailureOlderThan48HoursParks()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(
            db,
            "mailOLDROW00000000000000000001",
            createdAt: DateTime.UtcNow.AddHours(-48).AddMinutes(-1),
            attempts: 50);
        var before = Counter(PurchaseConfirmationMetrics.Parked, "smtp_error");
        var sender = new RecordingSender { Failure = new SmtpException("down") };

        await CreateService(db, sender).ProcessOneAsync();

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.FailedAt, Is.Not.Null);
        Assert.That(Counter(PurchaseConfirmationMetrics.Parked, "smtp_error"), Is.EqualTo(before + 1));
    }

    [Test]
    public async Task ProcessOne_NoAccountEmailUsesSlowTailAndNoRecipientReason()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(db, "mailNORECIPIENT000000000000001", attempts: 10);
        var row0 = await db.PurchaseConfirmationDeliveries.SingleAsync();
        row0.Recipient = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var userApi = new Mock<IUserApi>();
        userApi.Setup(api => api.UserEmailGetAsync("42", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GoogleUser { Email = "" });
        var service = new PurchaseConfirmationDeliveryService(
            db, new RecordingSender(), userApi.Object,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        await service.ProcessOneAsync();

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.FailedAt, Is.Null);
        Assert.That(row.LastError, Does.StartWith("[no_recipient] "));
        Assert.That(row.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(29)));
    }

    [Test]
    public async Task ProcessOne_IndexerFailureIsRecipientLookupFailed()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(db, "mailLOOKUPFAIL00000000000000001");
        var row0 = await db.PurchaseConfirmationDeliveries.SingleAsync();
        row0.Recipient = null;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var userApi = new Mock<IUserApi>();
        userApi.Setup(api => api.UserEmailGetAsync("42", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));
        var service = new PurchaseConfirmationDeliveryService(
            db, new RecordingSender(), userApi.Object,
            NullLogger<PurchaseConfirmationDeliveryService>.Instance);

        await service.ProcessOneAsync();

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.LastError, Does.StartWith("[recipient_lookup_failed] "));
    }

    [Test]
    public async Task ProcessOne_SuccessIncrementsSentCounter()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        await AddRow(db, "mailSENTCOUNTER00000000000000001");
        var before = PurchaseConfirmationMetrics.Sent.Value;

        await CreateService(db, new RecordingSender()).ProcessOneAsync();

        Assert.That(PurchaseConfirmationMetrics.Sent.Value, Is.EqualTo(before + 1));
    }

    [Test]
    public void Metrics_AllReasonSeriesExistAtStartup()
    {
        PurchaseConfirmationMetrics.Initialize();
        foreach (var reason in PurchaseConfirmationFailure.Reasons)
        {
            Assert.That(Counter(PurchaseConfirmationMetrics.Parked, reason), Is.GreaterThanOrEqualTo(0));
            Assert.That(Counter(PurchaseConfirmationMetrics.AttemptFailed, reason), Is.GreaterThanOrEqualTo(0));
        }
    }

    private sealed class TestDbException : DbException
    {
    }

    private sealed class RecordingSender : IPurchaseConfirmationEmailSender
    {
        public bool FailNext { get; set; }
        public bool AlwaysFail { get; set; }
        public Exception Failure { get; set; }
        public TimeSpan? DelayBeforeFail { get; set; }
        public List<string> Deliveries { get; } = [];
        public List<string> Recipients { get; } = [];

        public async Task SendAsync(
            string toEmail,
            string locale,
            PaymentEvent payment,
            string confirmationReference = null)
        {
            if (DelayBeforeFail.HasValue)
                await Task.Delay(DelayBeforeFail.Value);
            if (Failure != null)
                throw Failure;
            if (AlwaysFail || FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("temporary SMTP failure");
            }

            Deliveries.Add(confirmationReference);
            Recipients.Add(toEmail);
        }
    }
}
