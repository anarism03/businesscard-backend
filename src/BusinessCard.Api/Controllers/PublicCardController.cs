using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using BusinessCard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Controllers;

[ApiController]
[Route("api/cards")]
[AllowAnonymous]
[Produces("application/json")]
public class PublicCardController(AppDbContext db, AnalyticsService analytics) : ControllerBase
{
    /// <summary>
    /// Public card by user UUID <b>or</b> nickname (no login). UUID links (including physical NFC links) keep working
    /// forever; nickname lookup is case-insensitive. The response <c>id</c> is always the real UUID and includes the
    /// company's effective <c>brandColor</c>. Each open is logged as a scan with a server-generated UTC timestamp.
    /// </summary>
    /// <param name="id">User UUID or nickname, e.g. <c>9f0b153b-f04f-4f1a-bc79-f9dcc55e5189</c> or <c>anar-ismayilzade</c>.</param>
    /// <param name="source">Where the card was opened from: <c>nfc</c>, <c>qr</c>, <c>link</c>, <c>share</c>, <c>direct</c>.</param>
    /// <response code="404">CARD_NOT_FOUND — unknown UUID/nickname.</response>
    /// <response code="410">CARD_DEACTIVATED — the user or the company is deactivated.</response>
    [HttpGet("{id}")]
    [ProducesResponseType<PublicCardDto>(200)]
    [ProducesResponseType<ErrorResponse>(404)]
    [ProducesResponseType<ErrorResponse>(410)]
    public async Task<PublicCardDto> Get(string id, [FromQuery] string? source)
    {
        var key = (id ?? "").Trim();
        var query = db.Users
            .Include(u => u.Company)
            .Include(u => u.ContactInfos)
            .Include(u => u.SocialAccounts)
            .AsSplitQuery();

        var user = Guid.TryParse(key, out var guid)
            ? await query.FirstOrDefaultAsync(u => u.Id == guid)
            : await query.FirstOrDefaultAsync(u => u.Nickname == key.ToLowerInvariant());

        // Unknown id/nickname (or an account without a public card): 404 CARD_NOT_FOUND.
        // Existing card whose user or company is deactivated: 410 CARD_DEACTIVATED. Neither returns profile
        // data and neither records a scan.
        if (user is null || user.Company is null || user.Role == Role.SuperAdmin || !Validation.HasValidRoleAndCompany(user))
            throw new ApiException(404, "CARD_NOT_FOUND", "Card not found");
        if (!user.IsActive || !user.Company.IsActive)
            throw new ApiException(410, "CARD_DEACTIVATED", "Bu vizitkart deaktiv edilib.");

        var clientKey = $"{HttpContext.Connection.RemoteIpAddress}|{Request.Headers.UserAgent}";
        await analytics.RecordScanAsync(user, source, clientKey);

        // Never cache: brand color, profile and nickname changes must be visible on every device immediately.
        Response.Headers.CacheControl = "no-store, max-age=0";
        return user.ToPublicCard();
    }
}
