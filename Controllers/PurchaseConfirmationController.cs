using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Coflnet.Sky.EventBroker.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.EventBroker.Controllers;

[ApiController]
[Route("api/purchase-confirmations")]
public sealed class PurchaseConfirmationController : ControllerBase
{
    private readonly EventDbContext db;
    private readonly IConfiguration configuration;

    public PurchaseConfirmationController(
        EventDbContext db,
        IConfiguration configuration)
    {
        this.db = db;
        this.configuration = configuration;
    }

    [HttpGet("{provider}/{paymentId}/{confirmationType}")]
    public async Task<IActionResult> Get(
        string provider,
        string paymentId,
        string confirmationType)
    {
        var expected = configuration["PURCHASE_CONFIRMATIONS:READ_TOKEN"];
        if (expected?.Length < 32)
            return StatusCode(503);
        var supplied = Request.Headers.Authorization.ToString();
        if (!supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !FixedEquals(expected, supplied[7..]))
            return Unauthorized();

        var reference = MessageService.PurchaseConfirmationReference(
            provider,
            paymentId,
            confirmationType);
        var status = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .Where(item => item.Reference == reference)
            .Select(item => new { item.SentAt, item.FailedAt })
            .SingleOrDefaultAsync();
        if (status == null)
            return NotFound();
        if (status.FailedAt.HasValue)
            return Conflict();
        return status.SentAt.HasValue ? NoContent() : Accepted();
    }

    private static bool FixedEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                suppliedBytes);
    }
}
