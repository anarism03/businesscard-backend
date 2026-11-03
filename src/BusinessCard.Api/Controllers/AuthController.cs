using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using BusinessCard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BusinessCard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AuthController(
    AppDbContext db,
    PasswordService passwords,
    JwtTokenService tokens,
    CurrentUser current,
    IOptions<AuthOptions> authOptions) : ControllerBase
{
    /// <summary>Signs in. Company users send their company VÖEN; super admins leave it empty.</summary>
    /// <response code="401">INVALID_CREDENTIALS</response>
    /// <response code="403">ACCOUNT_INACTIVE (user or company deactivated)</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<LoginResponseDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    [ProducesResponseType<ErrorResponse>(401)]
    [ProducesResponseType<ErrorResponse>(403)]
    public async Task<LoginResponseDto> Login([FromBody] LoginDto dto)
    {
        var email = (dto.Email ?? "").Trim().ToLowerInvariant();
        var voen = (dto.CompanyVoen ?? "").Trim();
        if (email.Length == 0 || string.IsNullOrEmpty(dto.Password))
            throw new ApiException(401, "INVALID_CREDENTIALS", "Invalid credentials");

        User? user;
        if (voen.Length == 0)
        {
            user = await db.Users.Include(u => u.Company)
                .FirstOrDefaultAsync(u => u.CompanyId == null && u.Role == Role.SuperAdmin && u.Email == email);
        }
        else
        {
            user = await db.Users.Include(u => u.Company)
                .FirstOrDefaultAsync(u => u.Company != null && u.Company.Voen == voen && u.Email == email);
        }

        // Same response for unknown user / wrong password to avoid account enumeration.
        if (user is null || !passwords.Verify(user, dto.Password))
            throw new ApiException(401, "INVALID_CREDENTIALS", "Invalid credentials");
        if (!user.IsActive || user.Company is { IsActive: false })
            throw new ApiException(403, "ACCOUNT_INACTIVE", "This account is inactive");
        if (!Validation.HasValidRoleAndCompany(user))
            throw new ApiException(403, "INVALID_ACCOUNT_ROLE", "This account has an invalid role");

        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = user.CompanyId, UserId = user.Id, UserEmail = user.Email, UserName = user.FullName,
            Action = "LOGIN", EntityType = "Auth", EntityId = user.Id.ToString(),
        });
        db.AuditEnabled = false;
        await db.SaveChangesAsync();

        var (token, expiresAt) = tokens.Create(user);
        return new LoginResponseDto(token, user.MustChangePassword, expiresAt);
    }

    /// <summary>Current user profile plus password state (first login / days until expiry).</summary>
    [HttpGet("account-info")]
    [Authorize]
    [ProducesResponseType<AccountInfoDto>(200)]
    public async Task<AccountInfoDto> AccountInfo()
    {
        var me = await current.RequireAllowingPasswordChangeAsync();
        var user = await db.Users.Include(u => u.Company).Include(u => u.ContactInfos).Include(u => u.SocialAccounts)
            .AsSplitQuery().FirstAsync(u => u.Id == me.Id);
        var dto = Mapping.Fill(new AccountInfoDto(), user);
        dto.IsFirstLogin = user.MustChangePassword;
        var now = DateTime.UtcNow;
        dto.PasswordExpiresAt = PasswordPolicy.ExpiresAt(user, authOptions.Value);
        dto.DaysUntilPasswordExpiry = PasswordPolicy.DaysUntilExpiry(user, authOptions.Value, now);
        dto.PasswordChangeRequired = PasswordPolicy.BlockingCode(user, authOptions.Value, now);
        return dto;
    }

    /// <summary>Changes the caller's password. The current password is always required.</summary>
    /// <response code="400">Old password is incorrect / new password invalid.</response>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType<MessageDto>(200)]
    [ProducesResponseType<ErrorResponse>(400)]
    public async Task<MessageDto> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var me = await current.RequireAllowingPasswordChangeAsync();
        if (string.IsNullOrEmpty(dto.OldPassword) || !passwords.Verify(me, dto.OldPassword))
            throw ApiException.BadRequest("INVALID_OLD_PASSWORD", "Old password is incorrect");
        Validation.ValidatePassword(dto.NewPassword);
        if (dto.NewPassword == dto.OldPassword)
            throw ApiException.BadRequest("PASSWORD_REUSED", "New password must be different from the current password");

        me.PasswordHash = passwords.Hash(me, dto.NewPassword);
        me.MustChangePassword = false;
        me.PasswordChangedAt = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = me.CompanyId, UserId = me.Id, UserEmail = me.Email, UserName = me.FullName,
            Action = "CHANGE_PASSWORD", EntityType = "User", EntityId = me.Id.ToString(),
        });
        await db.SaveChangesAsync();
        return new MessageDto("Password changed successfully");
    }
}
