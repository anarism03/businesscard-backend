using System.Security.Claims;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BusinessCard.Api.Common;

/// <summary>Actor information the DbContext uses when writing audit rows.</summary>
public class AuditActor
{
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string? Name { get; set; }
    public Guid? CompanyId { get; set; }
}

/// <summary>
/// Resolves the authenticated user from the database on every request. Role, company, activity and
/// edit permission are always read from the database, never trusted from JWT claims.
/// </summary>
public class CurrentUser(
    IHttpContextAccessor http,
    AppDbContext db,
    AuditActor actor,
    IOptions<AuthOptions> authOptions,
    TimeProvider clock)
{
    private User? _user;

    /// <summary>
    /// Resolves the caller for a business operation. Accounts that must change their password (first login,
    /// admin reset or expired password) are rejected with 403 PASSWORD_CHANGE_REQUIRED / PASSWORD_EXPIRED.
    /// </summary>
    public async Task<User> RequireAsync(params Role[] roles)
    {
        var user = await LoadAsync();
        var blocking = PasswordPolicy.BlockingCode(user, authOptions.Value, clock.GetUtcNow().UtcDateTime);
        if (blocking is not null)
            throw new ApiException(403, blocking, blocking == PasswordPolicy.ExpiredCode
                ? "Your password has expired. Change it to continue."
                : "You must change your password before continuing.");
        if (roles.Length > 0 && !roles.Contains(user.Role)) throw ApiException.Forbidden();
        return user;
    }

    /// <summary>Only for account-info and change-password: allowed while a password change is pending.</summary>
    public Task<User> RequireAllowingPasswordChangeAsync() => LoadAsync();

    private async Task<User> LoadAsync()
    {
        if (_user != null) return _user;

        var principal = http.HttpContext?.User;
        var idText = principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal?.Identity?.IsAuthenticated != true || !Guid.TryParse(idText, out var id))
            throw ApiException.Unauthorized();

        var user = await db.Users.Include(u => u.Company).FirstOrDefaultAsync(u => u.Id == id)
                   ?? throw ApiException.Unauthorized();

        // Tokens issued before an administrative session revocation are no longer valid.
        var issuedAt = principal.FindFirstValue("iat");
        if (user.SessionsRevokedAt is { } revokedAt && long.TryParse(issuedAt, out var iat) &&
            DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime < revokedAt.AddSeconds(-1))
            throw ApiException.Unauthorized("Session expired");

        // Permissions depend on the stored role; unknown roles or role/company mismatches get no access.
        if (!Validation.HasValidRoleAndCompany(user))
            throw new ApiException(403, "INVALID_ACCOUNT_ROLE", "This account has an invalid role");

        if (!user.IsActive || (user.Company != null && !user.Company.IsActive))
            throw new ApiException(401, "ACCOUNT_INACTIVE", "This account is inactive");

        actor.UserId = user.Id;
        actor.Email = user.Email;
        actor.Name = user.FullName;
        actor.CompanyId = user.CompanyId;
        return _user = user;
    }

    /// <summary>Ensures the caller may manage the given company (SuperAdmin: any; CompanyAdmin: own).</summary>
    public static void EnsureCompanyAccess(User caller, Guid companyId)
    {
        if (caller.Role == Role.SuperAdmin) return;
        if (caller.Role == Role.CompanyAdmin && caller.CompanyId == companyId) return;
        throw ApiException.Forbidden();
    }
}
