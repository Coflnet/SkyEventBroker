using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Coflnet.Sky.EventBroker.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.EventBroker.Controllers;

public class PurchaseConfirmationControllerTests
{
    [Test]
    public async Task SentConfirmationIsReportedOnlyWithInternalToken()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var db = new EventDbContext(
            new DbContextOptionsBuilder<EventDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        db.PurchaseConfirmationDeliveries.Add(new()
        {
            Reference = MessageService.PurchaseConfirmationReference(
                "coflcoins", "42", "service_purchase"),
            CreatedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow,
            SentAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var token = new string('x', 32);
        var controller = new PurchaseConfirmationController(
            db,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string>
                {
                    ["PURCHASE_CONFIRMATIONS:READ_TOKEN"] = token
                }).Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        Assert.That(await controller.Get(
            "coflcoins", "42", "service_purchase"),
            Is.TypeOf<UnauthorizedResult>());
        controller.Request.Headers.Authorization = $"Bearer {token}";
        Assert.That(await controller.Get(
            "coflcoins", "42", "service_purchase"),
            Is.TypeOf<NoContentResult>());
    }

    private const string Token = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

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

    private static PurchaseConfirmationController CreateController(
        EventDbContext db,
        string configuredToken = Token,
        string suppliedToken = Token)
    {
        var values = new Dictionary<string, string>();
        if (configuredToken != null)
            values["PURCHASE_CONFIRMATIONS:READ_TOKEN"] = configuredToken;
        var controller = new PurchaseConfirmationController(
            db,
            new ConfigurationBuilder().AddInMemoryCollection(values).Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (suppliedToken != null)
            controller.Request.Headers.Authorization = $"Bearer {suppliedToken}";
        return controller;
    }

    private static PurchaseConfirmationDelivery Row(
        string reference,
        DateTime? failedAt = null,
        DateTime? sentAt = null,
        string payload = "{}",
        string lastError = null) => new()
    {
        Reference = reference,
        Recipient = "secret.buyer@example.test",
        Locale = "de-DE",
        Payload = payload,
        CreatedAt = DateTime.UtcNow.AddHours(-3),
        NextAttemptAt = DateTime.UtcNow.AddHours(-3),
        FailedAt = failedAt,
        SentAt = sentAt,
        Attempts = 4,
        LastError = lastError
    };

    [Test]
    public async Task ParkedListHidesRecipientAndScrubsAddressesFromLastError()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        db.PurchaseConfirmationDeliveries.AddRange(
            Row(
                "parkedONE",
                failedAt: DateTime.UtcNow,
                lastError: "[smtp_rejected_recipient] SmtpFailedRecipientException: Mailbox unavailable. The server response was: 550 <secret.buyer@example.test> no such user"),
            Row("stillRetrying"),
            Row("alreadySent", sentAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var result = await CreateController(db).Parked();

        var list = ((result as OkObjectResult)!.Value as IEnumerable<PurchaseConfirmationController.ParkedConfirmation>)!
            .ToList();
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Reference, Is.EqualTo("parkedONE"));
        Assert.That(list[0].FailureReason, Is.EqualTo("smtp_rejected_recipient"));
        Assert.That(list[0].Attempts, Is.EqualTo(4));
        Assert.That(list[0].LastError, Does.Not.Contain("example.test"));
        Assert.That(list[0].LastError, Does.Contain("[email]"));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(list);
        Assert.That(json, Does.Not.Contain("secret.buyer"));
        Assert.That(json, Does.Not.Contain("de-DE"));
        Assert.That(json.ToLowerInvariant(), Does.Not.Contain("payload"));
    }

    [Test]
    public void Sanitize_TruncatesTo500Characters()
    {
        Assert.That(
            PurchaseConfirmationController.Sanitize(new string('a', 900)),
            Has.Length.EqualTo(500));
    }

    [Test]
    public async Task RequeueResetsParkedRow()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        db.PurchaseConfirmationDeliveries.Add(Row("parkedTWO", failedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.That(await CreateController(db).Requeue("parkedTWO"), Is.TypeOf<NoContentResult>());

        var row = await db.PurchaseConfirmationDeliveries.AsNoTracking().SingleAsync();
        Assert.That(row.FailedAt, Is.Null);
        Assert.That(row.Attempts, Is.EqualTo(0));
        Assert.That(row.NextAttemptAt, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task RequeueRejectsUnknownSentAndPayloadlessRows()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;
        db.PurchaseConfirmationDeliveries.AddRange(
            Row("sentRow", sentAt: DateTime.UtcNow),
            Row("noPayload", failedAt: DateTime.UtcNow, payload: null));
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        Assert.That(await controller.Requeue("missing"), Is.TypeOf<NotFoundResult>());
        Assert.That(await controller.Requeue("sentRow"), Is.TypeOf<ConflictResult>());
        Assert.That(await controller.Requeue("noPayload"), Is.TypeOf<ConflictResult>());
    }

    [Test]
    public async Task OperatorEndpointsRequireTheToken()
    {
        var (connection, db) = await CreateDb();
        await using var _ = connection;
        await using var __ = db;

        foreach (var supplied in new string[] { null, "wrong" + Token })
        {
            var controller = CreateController(db, suppliedToken: supplied);
            Assert.That(await controller.Parked(), Is.TypeOf<UnauthorizedResult>());
            Assert.That(await controller.Requeue("x"), Is.TypeOf<UnauthorizedResult>());
            Assert.That(await controller.Get("p", "1", "t"), Is.TypeOf<UnauthorizedResult>());
        }

        foreach (var configured in new string[] { null, "", "short" })
        {
            var controller = CreateController(db, configuredToken: configured);
            Assert.That((await controller.Parked() as StatusCodeResult)!.StatusCode, Is.EqualTo(503));
            Assert.That((await controller.Requeue("x") as StatusCodeResult)!.StatusCode, Is.EqualTo(503));
            Assert.That((await controller.Get("p", "1", "t") as StatusCodeResult)!.StatusCode, Is.EqualTo(503));
        }
    }
}
