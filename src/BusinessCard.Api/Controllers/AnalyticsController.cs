using System.Text.Json;
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
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class AnalyticsController(CurrentUser current, AnalyticsService analytics) : ControllerBase
{
    private static AnalyticsQuery Q(Guid? companyId, Guid? employeeId, DateTimeOffset? startDate, DateTimeOffset? endDate) =>
        new(companyId, employeeId, startDate, endDate);

    /// <summary>Total scans in the range: <c>{ count, totalScans }</c>. Company admins are limited to their company.</summary>
    [HttpGet("scans/count")]
    [ProducesResponseType<CountDto>(200)]
    public async Task<CountDto> Count([FromQuery] Guid? companyId, [FromQuery] DateTimeOffset? startDate,
        [FromQuery] DateTimeOffset? endDate, [FromQuery] Guid? employeeId)
    {
        var me = await current.RequireAsync();
        return await analytics.CountAsync(me, Q(companyId, employeeId, startDate, endDate));
    }

    /// <summary>Daily scan counts <c>[{ date: "yyyy-MM-dd", count }]</c>; days are Asia/Baku calendar days, zero-filled.</summary>
    [HttpGet("scans/chart")]
    [ProducesResponseType<List<ChartPointDto>>(200)]
    public async Task<List<ChartPointDto>> Chart([FromQuery] Guid? companyId, [FromQuery] DateTimeOffset? startDate,
        [FromQuery] DateTimeOffset? endDate, [FromQuery] Guid? employeeId)
    {
        var me = await current.RequireAsync();
        return await analytics.ChartAsync(me, Q(companyId, employeeId, startDate, endDate));
    }

    /// <summary>Employees ordered by scan count in the range.</summary>
    [HttpGet("employees/ranking")]
    [ProducesResponseType<List<RankingDto>>(200)]
    public async Task<List<RankingDto>> Ranking([FromQuery] Guid? companyId, [FromQuery] DateTimeOffset? startDate,
        [FromQuery] DateTimeOffset? endDate)
    {
        var me = await current.RequireAsync();
        return await analytics.RankingAsync(me, Q(companyId, null, startDate, endDate));
    }

    /// <summary>
    /// Scan log, newest first. <c>scannedAt</c> is the server receipt time in UTC (ISO-8601 with <c>Z</c>);
    /// clients convert it to local time for display.
    /// </summary>
    [HttpGet("scans/logs")]
    [ProducesResponseType<PageResult<ScanLogDto>>(200)]
    public async Task<PageResult<ScanLogDto>> Logs([FromQuery] Guid? companyId, [FromQuery] Guid? employeeId,
        [FromQuery] DateTimeOffset? startDate, [FromQuery] DateTimeOffset? endDate,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var me = await current.RequireAsync();
        return await analytics.LogsAsync(me, Q(companyId, employeeId, startDate, endDate), page, pageSize);
    }
}

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class AuditLogController(CurrentUser current, AppDbContext db) : ControllerBase
{
    /// <summary>Audit trail, newest first. Super admins see everything; company admins see their company.</summary>
    [HttpGet]
    [ProducesResponseType<PageResult<AuditLogDto>>(200)]
    public async Task<PageResult<AuditLogDto>> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var me = await current.RequireAsync(Role.SuperAdmin, Role.CompanyAdmin);
        (page, pageSize) = Paging.Normalize(page, pageSize);

        var query = db.AuditLogs.AsNoTracking();
        if (me.Role == Role.CompanyAdmin) query = query.Where(a => a.CompanyId == me.CompanyId);

        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();
        var items = rows.Select(a => new AuditLogDto
        {
            Id = a.Id,
            CreatedAt = DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc),
            UserId = a.UserId,
            UserEmail = a.UserEmail,
            UserName = a.UserName,
            Action = a.Action,
            EntityType = a.EntityType,
            EntityId = a.EntityId,
            OldValues = Parse(a.OldValues),
            NewValues = Parse(a.NewValues),
        }).ToList();
        return new PageResult<AuditLogDto>(items, total, page, pageSize);
    }
}
