using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Services;

public class CompanyService(AppDbContext db, PasswordService passwords, UserService users)
{
    public async Task<CompanyDto> GetAsync(Guid id)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id) ?? throw ApiException.NotFound("Company not found");
        return await ToDtoAsync(company);
    }

    private async Task<CompanyDto> ToDtoAsync(Company company)
    {
        var used = await users.LimitedUsers(company.Id).CountAsync();
        var adminEmail = await db.Users
            .Where(u => u.CompanyId == company.Id && u.Role == Role.CompanyAdmin)
            .OrderBy(u => u.CreatedAt).Select(u => u.Email).FirstOrDefaultAsync();
        return company.ToDto(used, adminEmail);
    }

    public async Task<PageResult<CompanyDto>> ListAsync(int page, int pageSize)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var total = await db.Companies.CountAsync();
        var rows = await db.Companies
            .OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new
            {
                Company = c,
                Used = db.Users.Count(u => u.CompanyId == c.Id && u.Role == Role.Employee),
                Admin = db.Users.Where(u => u.CompanyId == c.Id && u.Role == Role.CompanyAdmin)
                    .OrderBy(u => u.CreatedAt).Select(u => u.Email).FirstOrDefault(),
            })
            .ToListAsync();
        return new PageResult<CompanyDto>(rows.Select(r => r.Company.ToDto(r.Used, r.Admin)).ToList(), total, page, pageSize);
    }

    public async Task<CreateCompanyResultDto> CreateAsync(CreateCompanyDto dto)
    {
        var name = Validation.RequireText(dto.Name, "name");
        var voen = Validation.NormalizeVoen(dto.Voen);
        var adminEmail = Validation.NormalizeEmail(dto.Email);
        if (dto.UserLimit < 0) throw ApiException.BadRequest("VALIDATION_ERROR", "userLimit cannot be negative");

        var company = new Company
        {
            Name = name,
            Voen = voen,
            LogoUrl = Validation.Patch(dto.LogoUrl, null),
            Address = Validation.Patch(dto.Address, null),
            Email = adminEmail,
            Phone = Validation.Patch(dto.Phone, null),
            UserLimit = dto.UserLimit,
            BrandColor = Validation.NormalizeBrandColor(dto.BrandColor),
        };

        var tempPassword = PasswordService.Generate();
        var admin = new User
        {
            CompanyId = company.Id,
            Email = adminEmail,
            Role = Role.CompanyAdmin,
            FirstName = name,
            LastName = "Admin",
            JobTitle = "Company Admin",
            IsActive = true,
            CanEdit = true,
            MustChangePassword = true,
        };
        admin.PasswordHash = passwords.Hash(admin, tempPassword);

        db.Companies.Add(company);
        db.Users.Add(admin);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, AppDbContext.VoenIndexName))
        {
            throw ApiException.Conflict("VOEN_TAKEN", "VOEN already exists");
        }

        return new CreateCompanyResultDto
        {
            Company = company.ToDto(0, adminEmail),
            Voen = voen,
            AdminEmail = adminEmail,
            AdminPassword = tempPassword,
        };
    }

    /// <summary>
    /// Updates a company. Company admins may change profile fields and brand color only;
    /// VÖEN and user limit are super-admin fields and are ignored for company admins.
    /// </summary>
    public async Task<CompanyDto> UpdateAsync(Guid id, UpdateCompanyDto dto, bool isSuperAdmin)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id) ?? throw ApiException.NotFound("Company not found");

        if (dto.Name is not null) company.Name = Validation.RequireText(dto.Name, "name");
        company.Address = Validation.Patch(dto.Address, company.Address);
        company.Phone = Validation.Patch(dto.Phone, company.Phone);
        company.LogoUrl = Validation.Patch(dto.LogoUrl, company.LogoUrl);
        company.NfcBaseUrl = Validation.Patch(dto.NfcBaseUrl, company.NfcBaseUrl);
        if (!string.IsNullOrWhiteSpace(dto.Email)) company.Email = Validation.NormalizeEmail(dto.Email);
        else if (dto.Email is not null) company.Email = null;

        // brandColor: omitted → unchanged; null → default; value → strict #RRGGBB.
        if (dto.BrandColorSpecified) company.BrandColor = Validation.NormalizeBrandColor(dto.BrandColor);

        if (isSuperAdmin)
        {
            if (!string.IsNullOrWhiteSpace(dto.Voen)) company.Voen = Validation.NormalizeVoen(dto.Voen);
            if (dto.UserLimit is { } limit) await ApplyLimitAsync(company, limit);
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, AppDbContext.VoenIndexName))
        {
            throw ApiException.Conflict("VOEN_TAKEN", "VOEN already exists");
        }
        // Public cards read the color live from the company row (responses are sent with no-store),
        // so every employee card reflects the change immediately; there is no server cache to invalidate.
        return await ToDtoAsync(company);
    }

    private async Task ApplyLimitAsync(Company company, int limit)
    {
        if (limit < 0) throw ApiException.BadRequest("VALIDATION_ERROR", "userLimit cannot be negative");
        var used = await users.LimitedUsers(company.Id).CountAsync();
        if (limit < used)
            throw ApiException.BadRequest("LIMIT_BELOW_USAGE", $"The limit cannot be lower than the current user count ({used})");
        company.UserLimit = limit;
    }

    public async Task<CompanyDto> SetLimitAsync(Guid id, int limit)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var company = await users.LockCompanyAsync(id);
        await ApplyLimitAsync(company, limit);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await ToDtoAsync(company);
    }

    public async Task<CompanyDto> SetActiveAsync(Guid id, bool isActive)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id) ?? throw ApiException.NotFound("Company not found");
        company.IsActive = isActive;
        await db.SaveChangesAsync();
        return await ToDtoAsync(company);
    }

    public async Task<DashboardDto> DashboardAsync()
    {
        var total = await db.Companies.CountAsync();
        var active = await db.Companies.CountAsync(c => c.IsActive);
        var usersCount = await db.Users.CountAsync(u => u.Role != Role.SuperAdmin);
        var totalScans = await db.ScanEvents.CountAsync();
        var recent = await ListAsync(1, 5);
        var top = await db.Companies
            .Select(c => new { c.Id, c.Name, c.LogoUrl, c.IsActive, Scans = db.ScanEvents.Count(s => s.CompanyId == c.Id) })
            .OrderByDescending(x => x.Scans).ThenBy(x => x.Name)
            .Take(5)
            .ToListAsync();
        return new DashboardDto(total, active, total - active, usersCount, totalScans, recent.Items,
            top.Select(t => new TopCompanyDto(t.Id, t.Name, t.LogoUrl, t.IsActive, t.Scans)).ToList());
    }
}
