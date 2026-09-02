using System;
using System.Collections.Generic;
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
}
