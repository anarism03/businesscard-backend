using System.Security.Cryptography;
using System.Text;
using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BusinessCard.Api.Services;

public class AnalyticsOptions
{
    /// <summary>IANA zone used to group chart days. Timestamps are always stored and returned in UTC.</summary>
    public string TimeZone { get; set; } = "Asia/Baku";
    /// <summary>Repeated opens of the same card from the same client within this window count once.</summary>
    public int DuplicateWindowSeconds { get; set; } = 30;
}

public record AnalyticsQuery(Guid? CompanyId, Guid? EmployeeId, DateTimeOffset? StartDate, DateTimeOffset? EndDate);

public class AnalyticsService(AppDbContext db, IOptions<AnalyticsOptions> options, TimeProvider clock)
{
    private readonly AnalyticsOptions _o = options.Value;

    public static readonly string[] KnownSources = ["nfc", "qr", "link", "share", "direct"];

    // ------------------------------------------------------------------ recording

    /// <summary>
    /// Records a card open. The timestamp is generated here, at request receipt, in UTC — clients never supply it.
    /// </summary>
    public async Task RecordScanAsync(User owner, string? source, string? clientKey)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var normalizedSource = NormalizeSource(source);
        var hash = clientKey is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientKey)))[..32];

        if (hash is not null && _o.DuplicateWindowSeconds > 0)
        {
            var since = now.AddSeconds(-_o.DuplicateWindowSeconds);
            var duplicate = await db.ScanEvents.AnyAsync(s =>
                s.UserId == owner.Id && s.ClientHash == hash && s.Source == normalizedSource && s.ScannedAt >= since);
            if (duplicate) return;
        }

        db.ScanEvents.Add(new ScanEvent
        {
            UserId = owner.Id,
            CompanyId = owner.CompanyId!.Value,
            ScannedAt = now,
            Source = normalizedSource,
            ClientHash = hash,
        });
        var auditWas = db.AuditEnabled;
        db.AuditEnabled = false;
        try { await db.SaveChangesAsync(); }
        finally { db.AuditEnabled = auditWas; }
    }

    public static string NormalizeSource(string? source)
    {
        var s = (source ?? "").Trim().ToLowerInvariant();
        return KnownSources.Contains(s) ? s : "link";
    }

    // ------------------------------------------------------------------ queries

    private IQueryable<ScanEvent> Scoped(User caller, AnalyticsQuery q)
    {
        Guid? companyId = q.CompanyId;
        Guid? employeeId = q.EmployeeId;

        switch (caller.Role)
        {
            case Role.CompanyAdmin:
                if (companyId is { } requested && requested != caller.CompanyId) throw ApiException.Forbidden();
                companyId = caller.CompanyId;
                break;
            case Role.Employee:
                if (companyId is { } c && c != caller.CompanyId) throw ApiException.Forbidden();
                if (employeeId is { } e && e != caller.Id) throw ApiException.Forbidden();
                companyId = caller.CompanyId;
                employeeId = caller.Id;
                break;
        }

        var query = db.ScanEvents.AsNoTracking().AsQueryable();
        if (companyId is { } cid) query = query.Where(s => s.CompanyId == cid);
        if (employeeId is { } eid) query = query.Where(s => s.UserId == eid);
        if (q.StartDate is { } start)
        {
            var from = start.UtcDateTime;
            query = query.Where(s => s.ScannedAt >= from);
        }
        if (q.EndDate is { } end)
        {
            var to = end.UtcDateTime;
            query = query.Where(s => s.ScannedAt <= to);
        }
        return query;
    }

    public async Task<CountDto> CountAsync(User caller, AnalyticsQuery q)
    {
        var count = await Scoped(caller, q).CountAsync();
        return new CountDto(count, count);
    }

    public async Task<List<ChartPointDto>> ChartAsync(User caller, AnalyticsQuery q)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_o.TimeZone);
        var now = clock.GetUtcNow();
        var end = q.EndDate ?? now;
        var start = q.StartDate ?? end.AddDays(-30);
        if (start > end) throw ApiException.BadRequest("INVALID_RANGE", "startDate must be before endDate");

        var scoped = Scoped(caller, q with { StartDate = start, EndDate = end });
        var timestamps = await scoped.Select(s => s.ScannedAt).ToListAsync();
        var counts = timestamps
            .GroupBy(t => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), tz)))
            .ToDictionary(g => g.Key, g => g.Count());

        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, tz).DateTime);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, tz).DateTime);
        if (lastDay.DayNumber - firstDay.DayNumber > 3700)
            throw ApiException.BadRequest("INVALID_RANGE", "Date range is too large");

        var points = new List<ChartPointDto>();
        for (var d = firstDay; d <= lastDay; d = d.AddDays(1))
            points.Add(new ChartPointDto(d.ToString("yyyy-MM-dd"), counts.GetValueOrDefault(d)));
        return points;
    }

    public async Task<List<RankingDto>> RankingAsync(User caller, AnalyticsQuery q)
    {
        var scoped = Scoped(caller, q with { EmployeeId = caller.Role == Role.Employee ? caller.Id : null });
        var counts = await scoped.GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var companyId = caller.Role == Role.SuperAdmin ? q.CompanyId : caller.CompanyId;
        var usersQuery = db.Users.AsNoTracking().Where(u => u.Role != Role.SuperAdmin);
        if (companyId is { } cid) usersQuery = usersQuery.Where(u => u.CompanyId == cid);
        else usersQuery = usersQuery.Where(u => counts.Keys.Contains(u.Id));
        if (caller.Role == Role.Employee) usersQuery = usersQuery.Where(u => u.Id == caller.Id);

        var people = await usersQuery
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.MiddleName, u.JobTitle, u.PhotoUrl })
            .ToListAsync();

        return people
            .Select(u => new RankingDto(u.Id,
                string.Join(" ", new[] { u.FirstName, u.LastName, u.MiddleName }.Where(s => !string.IsNullOrWhiteSpace(s))),
                u.JobTitle, u.PhotoUrl, counts.GetValueOrDefault(u.Id)))
            .OrderByDescending(r => r.ScanCount).ThenBy(r => r.FullName)
            .ToList();
    }

    public async Task<PageResult<ScanLogDto>> LogsAsync(User caller, AnalyticsQuery q, int page, int pageSize)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var scoped = Scoped(caller, q);
        var total = await scoped.CountAsync();
        var items = await scoped
            .OrderByDescending(s => s.ScannedAt).ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new ScanLogDto
            {
                Id = s.Id,
                ScannedAt = s.ScannedAt,
                EmployeeId = s.UserId,
                EmployeeName = (s.User!.FirstName + " " + s.User.LastName).Trim(),
                JobTitle = s.User.JobTitle,
                PhotoUrl = s.User.PhotoUrl,
                Source = s.Source,
            })
            .ToListAsync();
        foreach (var item in items) item.ScannedAt = DateTime.SpecifyKind(item.ScannedAt, DateTimeKind.Utc);
        return new PageResult<ScanLogDto>(items, total, page, pageSize);
    }
}
