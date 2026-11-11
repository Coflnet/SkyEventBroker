using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using Coflnet.Sky.EventBroker.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coflnet.Sky.EventBroker.Controllers;

[ApiController]
[Route("api/purchase-confirmations")]
public sealed class PurchaseConfirmationController : ControllerBase
{
    private readonly EventDbContext db;
    private readonly IConfiguration configuration;
    private readonly ILogger<PurchaseConfirmationController> logger;

    public PurchaseConfirmationController(
        EventDbContext db,
        IConfiguration configuration,
        ILogger<PurchaseConfirmationController> logger = null)
    {
        this.db = db;
        this.configuration = configuration;
        this.logger = logger ?? NullLogger<PurchaseConfirmationController>.Instance;
    }

    [HttpGet("{provider}/{paymentId}/{confirmationType}")]
    public async Task<IActionResult> Get(
        string provider,
        string paymentId,
        string confirmationType)
    {
        var denied = Authorize();
        if (denied != null)
            return denied;

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

    /// <summary>
    /// Lists rows that need an operator. Never exposes the recipient, payload or locale, and
    /// scrubs e-mail addresses from the error text (SMTP errors can echo the address).
    /// </summary>
    [HttpGet("parked")]
    public async Task<IActionResult> Parked([FromQuery] int limit = 50)
    {
        var denied = Authorize();
        if (denied != null)
            return denied;

        limit = Math.Clamp(limit, 1, 200);
        var rows = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .Where(item => item.FailedAt != null && item.SentAt == null)
            .OrderByDescending(item => item.FailedAt)
            .Take(limit)
            .Select(item => new
            {
                item.Reference,
                item.CreatedAt,
                item.FailedAt,
                item.Attempts,
                item.LastError
            })
            .ToListAsync();
        return Ok(rows.Select(item => new ParkedConfirmation(
            item.Reference,
            item.CreatedAt,
            item.FailedAt,
            item.Attempts,
            PurchaseConfirmationFailure.ParseReason(item.LastError),
            Sanitize(item.LastError))));
    }

    /// <summary>
    /// Gives a parked (or still retrying) row a fresh retry budget. 404 unknown reference,
    /// 409 already sent or payload gone, 204 on success.
    /// </summary>
    [HttpPost("{reference}/requeue")]
    public async Task<IActionResult> Requeue(string reference)
    {
        var denied = Authorize();
        if (denied != null)
            return denied;

        var row = await db.PurchaseConfirmationDeliveries
            .AsNoTracking()
            .Where(item => item.Reference == reference)
            .Select(item => new { item.Id, item.SentAt, HasPayload = item.Payload != null })
            .SingleOrDefaultAsync();
        if (row == null)
            return NotFound();
        if (row.SentAt.HasValue || !row.HasPayload)
            return Conflict();

        // Guarded on SentAt so a concurrent successful send cannot be reopened.
        var updated = await db.PurchaseConfirmationDeliveries
            .Where(item => item.Id == row.Id && item.SentAt == null && item.Payload != null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.FailedAt, (DateTime?)null)
                .SetProperty(item => item.NextAttemptAt, DateTime.UtcNow)
                .SetProperty(item => item.Attempts, 0));
        if (updated == 0)
            return Conflict();
        logger.LogInformation("Purchase confirmation {Reference} was requeued", reference);
        return NoContent();
    }

    public sealed record ParkedConfirmation(
        string Reference,
        DateTime CreatedAt,
        DateTime? FailedAt,
        int Attempts,
        string FailureReason,
        string LastError);

    private static readonly Regex EmailAddress = new(
        @"[^\s<>""'(),;:]+@[^\s<>""'(),;:]+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    internal static string Sanitize(string error)
    {
        if (string.IsNullOrEmpty(error))
            return error;
        var scrubbed = EmailAddress.Replace(error, "[email]");
        return scrubbed.Length > 500 ? scrubbed[..500] : scrubbed;
    }

    /// <summary>Null when the bearer token is valid, otherwise the response to return.</summary>
    private IActionResult Authorize()
    {
        var expected = configuration["PURCHASE_CONFIRMATIONS:READ_TOKEN"];
        if (string.IsNullOrEmpty(expected) || expected.Length < 32)
            return StatusCode(503);
        var supplied = Request.Headers.Authorization.ToString();
        if (!supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !FixedEquals(expected, supplied[7..]))
            return Unauthorized();
        return null;
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
