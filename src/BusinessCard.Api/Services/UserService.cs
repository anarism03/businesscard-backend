using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Services;

public class UserService(AppDbContext db, PasswordService passwords)
{
    public const int MaxContactItems = 100;

    public IQueryable<User> WithDetails() => db.Users
        .Include(u => u.Company)
        .Include(u => u.ContactInfos)
        .Include(u => u.SocialAccounts)
        .AsSplitQuery();

    public async Task<User> GetDetailedAsync(Guid id) =>
        await WithDetails().FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("User not found");

    /// <summary>Who may read a user: self, admins of the same company, super admins.</summary>
    public static void EnsureCanView(User caller, User target)
    {
        if (caller.Id == target.Id || caller.Role == Role.SuperAdmin) return;
        if (caller.Role == Role.CompanyAdmin && target.CompanyId == caller.CompanyId) return;
        throw ApiException.Forbidden();
    }

    /// <summary>Company admins manage users of their own company (super admins: any non-super-admin user).</summary>
    public static void EnsureCanManage(User caller, User target)
    {
        if (target.Role == Role.SuperAdmin) throw ApiException.Forbidden();
        if (caller.Role == Role.SuperAdmin) return;
        if (caller.Role == Role.CompanyAdmin && target.CompanyId == caller.CompanyId) return;
        throw ApiException.Forbidden();
    }

    /// <summary>
    /// Whether a user may edit their own profile/images: company admins and super admins always,
    /// employees only with <c>canEdit = true</c>. Unknown roles never.
    /// </summary>
    public static bool CanEditOwnProfile(User user) => user.Role switch
    {
        Role.SuperAdmin or Role.CompanyAdmin => true,
        Role.Employee => user.CanEdit,
        _ => false,
    };

    /// <summary>Users that count against the company limit: Employee role only, active and inactive.</summary>
    public IQueryable<User> LimitedUsers(Guid companyId) =>
        db.Users.Where(u => u.CompanyId == companyId && u.Role == Role.Employee);

    public async Task<PageResult<UserDto>> ListByCompanyAsync(Guid companyId, int page, int pageSize)
    {
        (page, pageSize) = Paging.Normalize(page, pageSize);
        var query = WithDetails().Where(u => u.CompanyId == companyId);
        var total = await query.CountAsync();
        var items = await query
            .OrderBy(u => u.Role).ThenBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        return new PageResult<UserDto>(items.Select(u => u.ToDto()).ToList(), total, page, pageSize);
    }

    // ------------------------------------------------------------------ create

    public async Task<CreateUserResultDto> CreateAsync(User caller, CreateUserDto dto)
    {
        var companyId = caller.Role == Role.SuperAdmin ? dto.CompanyId : caller.CompanyId!.Value;
        if (caller.Role != Role.SuperAdmin && dto.CompanyId != Guid.Empty && dto.CompanyId != companyId)
            throw ApiException.Forbidden("You can only create users in your own company");
        var role = Validation.RequireDefinedRole(dto.Role);
        if (role == Role.SuperAdmin)
            throw new ApiException(403, "ROLE_NOT_ALLOWED", "Super admins cannot be created here");
        if (caller.Role == Role.CompanyAdmin && role != Role.Employee)
            throw new ApiException(403, "ROLE_NOT_ALLOWED", "Company admins can only create employees");

        var email = Validation.NormalizeEmail(dto.Email);
        string? generated = null;
        var password = dto.Password;
        if (string.IsNullOrWhiteSpace(password)) password = generated = PasswordService.Generate();
        Validation.ValidatePassword(password);

        await using var tx = await db.Database.BeginTransactionAsync();
        // Lock the company row so concurrent creates/imports cannot exceed the limit.
        var company = await LockCompanyAsync(companyId);

        var user = new User
        {
            CompanyId = companyId,
            Email = email,
            Role = role,
            IsActive = dto.IsActive,
            CanEdit = false,
            MustChangePassword = true,
            FirstName = Validation.RequireText(dto.FirstName, "firstName", 100),
            LastName = Validation.RequireText(dto.LastName, "lastName", 100),
            MiddleName = Validation.Patch(dto.MiddleName, null),
            JobTitle = Validation.Patch(dto.JobTitle, null),
            Phone1 = Validation.Patch(dto.Phone1, null),
            Phone2 = Validation.Patch(dto.Phone2, null),
            WhatsappPhone = Validation.Patch(dto.WhatsappPhone, null),
            ExtensionNumber = Validation.Patch(dto.ExtensionNumber, null),
            AdditionalInfo = Validation.Patch(dto.AdditionalInfo, null),
            GoogleMapsUrl = Validation.Patch(dto.GoogleMapsUrl, null),
            Address = Validation.Patch(dto.Address, null),
            Birthday = Validation.Patch(dto.Birthday, null),
            LinkedinUrl = Validation.Patch(dto.LinkedinUrl, null),
            FacebookUrl = Validation.Patch(dto.FacebookUrl, null),
            InstagramUrl = Validation.Patch(dto.InstagramUrl, null),
        };
        user.PasswordHash = passwords.Hash(user, password);
        ReplaceCollections(user, dto.ContactInfos, dto.SocialAccounts);

        await EnsureCapacityAsync(company, user.Role == Role.Employee ? 1 : 0);
        db.Users.Add(user);
        await SaveUserAsync();
        await tx.CommitAsync();

        user.Company = company;
        var result = Mapping.Fill(new CreateUserResultDto(), user);
        result.TemporaryPassword = generated;
        return result;
    }

    public async Task<Company> LockCompanyAsync(Guid companyId)
    {
        var company = await db.Companies
            .FromSqlInterpolated($"SELECT * FROM \"Companies\" WHERE \"Id\" = {companyId} FOR UPDATE")
            .FirstOrDefaultAsync() ?? throw ApiException.NotFound("Company not found");
        return company;
    }

    public async Task EnsureCapacityAsync(Company company, int adding)
    {
        if (adding == 0) return;
        var used = await LimitedUsers(company.Id).CountAsync();
        if (used + adding > company.UserLimit)
            throw new ApiException(400, "USER_LIMIT_EXCEEDED", $"User limit exceeded ({used}/{company.UserLimit})");
    }

    /// <summary>Saves and maps the per-company e-mail unique index to a 409.</summary>
    public async Task SaveUserAsync()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, AppDbContext.EmailIndexName))
        {
            throw ApiException.Conflict("EMAIL_TAKEN", "Email already in use");
        }
    }

    // ------------------------------------------------------------------ update

    public async Task<UserDto> UpdateProfileAsync(User target, UpdateUserProfileDto dto)
    {
        if (dto.FirstName is not null) target.FirstName = Validation.RequireText(dto.FirstName, "firstName", 100);
        if (dto.LastName is not null) target.LastName = Validation.RequireText(dto.LastName, "lastName", 100);
        target.MiddleName = Validation.Patch(dto.MiddleName, target.MiddleName);
        target.JobTitle = Validation.Patch(dto.JobTitle, target.JobTitle);
        target.Phone1 = Validation.Patch(dto.Phone1, target.Phone1);
        target.Phone2 = Validation.Patch(dto.Phone2, target.Phone2);
        target.WhatsappPhone = Validation.Patch(dto.WhatsappPhone, target.WhatsappPhone);
        target.ExtensionNumber = Validation.Patch(dto.ExtensionNumber, target.ExtensionNumber);
        target.AdditionalInfo = Validation.Patch(dto.AdditionalInfo, target.AdditionalInfo);
        target.PhotoUrl = Validation.Patch(dto.PhotoUrl, target.PhotoUrl);
        target.CardBackgroundUrl = Validation.Patch(dto.CardBackgroundUrl, target.CardBackgroundUrl);
        target.GoogleMapsUrl = Validation.Patch(dto.GoogleMapsUrl, target.GoogleMapsUrl);
        target.Address = Validation.Patch(dto.Address, target.Address);
        target.Birthday = Validation.Patch(dto.Birthday, target.Birthday);
        target.LinkedinUrl = Validation.Patch(dto.LinkedinUrl, target.LinkedinUrl);
        target.FacebookUrl = Validation.Patch(dto.FacebookUrl, target.FacebookUrl);
        target.InstagramUrl = Validation.Patch(dto.InstagramUrl, target.InstagramUrl);
        ValidateImageValue(target.PhotoUrl);
        ValidateImageValue(target.CardBackgroundUrl);
        ReplaceCollections(target, dto.ContactInfos, dto.SocialAccounts);

        await SaveUserAsync();
        return target.ToDto();
    }

    private static void ValidateImageValue(string? value)
    {
        if (value is null) return;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("INVALID_IMAGE", "Only image data URLs are allowed");
        if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("INVALID_IMAGE", "Invalid image URL");
    }

    /// <summary>
    /// Replaces contact / social lists when supplied, preserving order. If the incoming list equals the stored one
    /// nothing is touched (avoids noisy audit rows). Metadata rows such as <c>__setclapp_*__:</c> labels are stored verbatim.
    /// </summary>
    public void ReplaceCollections(User user, List<ContactInfoDto>? contacts, List<SocialAccountDto>? socials)
    {
        if (contacts is not null)
        {
            if (contacts.Count > MaxContactItems) throw ApiException.BadRequest("VALIDATION_ERROR", "Too many contact items");
            var incoming = contacts
                .Where(c => !string.IsNullOrWhiteSpace(c.ContactType) && c.Value is not null)
                .Select((c, i) => new ContactInfo
                {
                    UserId = user.Id,
                    ContactType = c.ContactType!.Trim(),
                    Value = c.Value!,
                    Label = c.Label,
                    DisplayOrder = c.DisplayOrder != 0 ? c.DisplayOrder : i,
                })
                .OrderBy(c => c.DisplayOrder)
                .ToList();
            var current = user.ContactInfos.OrderBy(c => c.DisplayOrder).ToList();
            var same = current.Count == incoming.Count && current.Zip(incoming).All(p =>
                p.First.ContactType == p.Second.ContactType && p.First.Value == p.Second.Value &&
                p.First.Label == p.Second.Label && p.First.DisplayOrder == p.Second.DisplayOrder);
            if (!same)
            {
                db.ContactInfos.RemoveRange(user.ContactInfos);
                user.ContactInfos.Clear();
                user.ContactInfos.AddRange(incoming);
                if (db.Entry(user).State != EntityState.Added) db.ContactInfos.AddRange(incoming);
            }
        }

        if (socials is not null)
        {
            if (socials.Count > MaxContactItems) throw ApiException.BadRequest("VALIDATION_ERROR", "Too many social accounts");
            var incoming = socials
                .Where(s => !string.IsNullOrWhiteSpace(s.PlatformName) && !string.IsNullOrWhiteSpace(s.ProfileUrl))
                .Select((s, i) => new SocialAccount
                {
                    UserId = user.Id,
                    PlatformName = s.PlatformName!.Trim(),
                    ProfileUrl = s.ProfileUrl!.Trim(),
                    IconUrl = string.IsNullOrWhiteSpace(s.IconUrl) ? null : s.IconUrl,
                    DisplayOrder = i,
                })
                .ToList();
            var current = user.SocialAccounts.OrderBy(s => s.DisplayOrder).ToList();
            var same = current.Count == incoming.Count && current.Zip(incoming).All(p =>
                p.First.PlatformName == p.Second.PlatformName && p.First.ProfileUrl == p.Second.ProfileUrl &&
                p.First.IconUrl == p.Second.IconUrl);
            if (!same)
            {
                db.SocialAccounts.RemoveRange(user.SocialAccounts);
                user.SocialAccounts.Clear();
                user.SocialAccounts.AddRange(incoming);
                if (db.Entry(user).State != EntityState.Added) db.SocialAccounts.AddRange(incoming);
            }
        }
    }

    // ------------------------------------------------------------------ admin actions

    public async Task<UserDto> SetActiveAsync(User caller, Guid id, bool isActive)
    {
        var target = await GetDetailedAsync(id);
        EnsureCanManage(caller, target);
        if (target.Id == caller.Id && !isActive)
            throw ApiException.BadRequest("CANNOT_DEACTIVATE_SELF", "You cannot deactivate your own account");
        if (target.IsActive != isActive)
        {
            target.IsActive = isActive;
            if (!isActive) target.SessionsRevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return target.ToDto();
    }

    public async Task<UserDto> SetCanEditAsync(User caller, Guid id, bool canEdit)
    {
        var target = await GetDetailedAsync(id);
        EnsureCanManage(caller, target);
        target.CanEdit = canEdit;
        await db.SaveChangesAsync();
        return target.ToDto();
    }

    public async Task<ResetPasswordResultDto> ResetPasswordAsync(User caller, Guid id, ResetPasswordDto dto)
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("User not found");
        EnsureCanManage(caller, target);
        var password = string.IsNullOrWhiteSpace(dto.NewPassword) ? PasswordService.Generate() : dto.NewPassword;
        Validation.ValidatePassword(password);
        target.PasswordHash = passwords.Hash(target, password);
        target.MustChangePassword = true;
        target.PasswordChangedAt = DateTime.UtcNow;
        target.SessionsRevokedAt = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = target.CompanyId, UserId = caller.Id, UserEmail = caller.Email, UserName = caller.FullName,
            Action = "RESET_PASSWORD", EntityType = "User", EntityId = target.Id.ToString(),
        });
        await db.SaveChangesAsync();
        return new ResetPasswordResultDto("Password has been reset", password);
    }
}
