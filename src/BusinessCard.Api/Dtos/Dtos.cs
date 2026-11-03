using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BusinessCard.Api.Domain;

namespace BusinessCard.Api.Dtos;

// ---------------------------------------------------------------- Auth

public class LoginDto
{
    /// <example>admin@company.az</example>
    public string? Email { get; set; }
    /// <example>P@ssw0rd!</example>
    public string? Password { get; set; }
    /// <summary>Company VÖEN. Required for company admins and employees; omitted for super admins.</summary>
    /// <example>1234567890</example>
    public string? CompanyVoen { get; set; }
}

public record LoginResponseDto(string AccessToken, bool IsFirstLogin, DateTime ExpiresAt);

public class ChangePasswordDto
{
    public string? OldPassword { get; set; }
    [Required, MinLength(6)]
    public string NewPassword { get; set; } = "";
}

public record MessageDto(string Message);

public class AccountInfoDto : UserDto
{
    public bool IsFirstLogin { get; set; }
    /// <summary>Days left before the password must be changed; null when expiry is disabled.</summary>
    public int? DaysUntilPasswordExpiry { get; set; }
    public DateTime? PasswordExpiresAt { get; set; }
    /// <summary>PASSWORD_CHANGE_REQUIRED / PASSWORD_EXPIRED while business requests are blocked, otherwise null.</summary>
    public string? PasswordChangeRequired { get; set; }
}

// ---------------------------------------------------------------- Companies

public class CreateCompanyDto
{
    /// <example>SetClapp MMC</example>
    public string? Name { get; set; }
    /// <example>1234567890</example>
    public string? Voen { get; set; }
    public string? LogoUrl { get; set; }
    public string? Address { get; set; }
    /// <summary>Contact e-mail; also used as the auto-created company admin login.</summary>
    /// <example>admin@setclapp.az</example>
    public string? Email { get; set; }
    public string? Phone { get; set; }
    /// <example>50</example>
    public int UserLimit { get; set; }
    /// <summary>Optional initial brand color (#RRGGBB). Defaults to #1b4a75.</summary>
    /// <example>#15803d</example>
    public string? BrandColor { get; set; }
}

/// <summary>
/// Company update. For most fields <c>null</c>/omitted means "keep current value".
/// <c>brandColor</c>: omitted = unchanged, <c>null</c> = reset to default <c>#1b4a75</c>, otherwise strict <c>#RRGGBB</c>.
/// </summary>
public class UpdateCompanyDto
{
    private string? _brandColor;

    public string? Name { get; set; }
    /// <summary>Only super admins may change the VÖEN.</summary>
    public string? Voen { get; set; }
    public string? Address { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    /// <summary>Image URL or data URL; empty string removes the logo.</summary>
    public string? LogoUrl { get; set; }
    /// <summary>Only super admins may change the limit.</summary>
    public int? UserLimit { get; set; }
    public string? NfcBaseUrl { get; set; }

    /// <example>#15803d</example>
    public string? BrandColor
    {
        get => _brandColor;
        set { _brandColor = value; BrandColorSpecified = true; }
    }

    [JsonIgnore] public bool BrandColorSpecified { get; private set; }
}

public class CompanyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Voen { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string? Address { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int UserLimit { get; set; }
    /// <summary>Users counted against the limit (all employees, active and inactive).</summary>
    public int UsedCount { get; set; }
    public string? NfcBaseUrl { get; set; }
    /// <summary>Always the effective color (never null).</summary>
    /// <example>#1b4a75</example>
    public string BrandColor { get; set; } = Domain.Company.DefaultBrandColor;
    public bool IsActive { get; set; }
    public string? AdminEmail { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateCompanyResultDto
{
    public CompanyDto Company { get; set; } = new();
    public string Voen { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    /// <summary>One-time temporary password. It is shown only in this response and must be changed at first login.</summary>
    public string AdminPassword { get; set; } = "";
}

public record TopCompanyDto(Guid CompanyId, string CompanyName, string? LogoUrl, bool IsActive, int ScanCount);

public record DashboardDto(
    int CompaniesCount,
    int ActiveCompaniesCount,
    int InactiveCompaniesCount,
    int UsersCount,
    int TotalScans,
    IReadOnlyList<CompanyDto> RecentCompanies,
    IReadOnlyList<TopCompanyDto> TopCompaniesByScans);

// ---------------------------------------------------------------- Users

public class ContactInfoDto
{
    public string? ContactType { get; set; }
    public string? Value { get; set; }
    public string? Label { get; set; }
    public int DisplayOrder { get; set; }
}

public class SocialAccountDto
{
    public string? PlatformName { get; set; }
    public string? ProfileUrl { get; set; }
    public string? IconUrl { get; set; }
}

public class CreateUserDto
{
    public Guid CompanyId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public string? JobTitle { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? ExtensionNumber { get; set; }
    public string? AdditionalInfo { get; set; }
    public string? Email { get; set; }
    /// <summary>Optional. When empty a temporary password is generated and returned once.</summary>
    public string? Password { get; set; }
    public Role Role { get; set; } = Role.Employee;
    public bool IsActive { get; set; } = true;
    public string? GoogleMapsUrl { get; set; }
    public string? Address { get; set; }
    public string? Birthday { get; set; }
    public string? LinkedinUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public List<SocialAccountDto>? SocialAccounts { get; set; }
    public List<ContactInfoDto>? ContactInfos { get; set; }
}

/// <summary>
/// Profile update. <c>null</c>/omitted keeps the current value; an empty string clears a text/image field.
/// Collections replace the stored list (order preserved) when supplied.
/// </summary>
public class UpdateUserProfileDto
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public string? JobTitle { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? ExtensionNumber { get; set; }
    public string? AdditionalInfo { get; set; }
    public string? PhotoUrl { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? CardBackgroundUrl { get; set; }
    public string? Address { get; set; }
    public string? Birthday { get; set; }
    public string? LinkedinUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public List<SocialAccountDto>? SocialAccounts { get; set; }
    public List<ContactInfoDto>? ContactInfos { get; set; }
}

public class ResetPasswordDto
{
    /// <summary>Optional. When empty a temporary password is generated.</summary>
    public string? NewPassword { get; set; }
}

public record ResetPasswordResultDto(string Message, string NewPassword);

public class UserDto
{
    public Guid Id { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyLogoUrl { get; set; }
    /// <summary>The company's effective brand color (null only for super admins).</summary>
    public string? BrandColor { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string FullName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? ExtensionNumber { get; set; }
    public string? AdditionalInfo { get; set; }
    public string Email { get; set; } = "";
    public Role Role { get; set; }
    public bool IsActive { get; set; }
    public bool CanEdit { get; set; }
    public string? PhotoUrl { get; set; }
    public string? CardBackgroundUrl { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? Address { get; set; }
    public string? Birthday { get; set; }
    public string? LinkedinUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    /// <summary>Always present; null when the user has no nickname.</summary>
    public string? Nickname { get; set; }
    public List<SocialAccountDto> SocialAccounts { get; set; } = [];
    public List<ContactInfoDto> ContactInfos { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public class CreateUserResultDto : UserDto
{
    /// <summary>Returned once when the server generated the temporary password.</summary>
    public string? TemporaryPassword { get; set; }
}

// ---------------------------------------------------------------- Nickname

/// <summary>Body for both nickname endpoints. The <c>nickname</c> property is mandatory; send <c>null</c> to remove.</summary>
public class NicknameRequest
{
    private string? _nickname;

    /// <example>anar-ismayilzade</example>
    public string? Nickname
    {
        get => _nickname;
        set { _nickname = value; NicknameSpecified = true; }
    }

    [JsonIgnore] public bool NicknameSpecified { get; private set; }
}

public record NicknameResponse(Guid Id, string? Nickname);

// ---------------------------------------------------------------- Public card

public record PublicCompanyDto(Guid Id, string Name, string? LogoUrl, string BrandColor, string? Address, string? Phone, string? Email);

public class PublicCardDto
{
    /// <summary>Always the real user UUID, even when the card was opened by nickname.</summary>
    public Guid Id { get; set; }
    public string? Nickname { get; set; }
    public Guid CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyLogoUrl { get; set; }
    /// <summary>The owning company's effective brand color.</summary>
    /// <example>#15803d</example>
    public string BrandColor { get; set; } = Domain.Company.DefaultBrandColor;
    public PublicCompanyDto? Company { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string FullName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? ExtensionNumber { get; set; }
    public string? AdditionalInfo { get; set; }
    public string? Email { get; set; }
    public string? PhotoUrl { get; set; }
    public string? CardBackgroundUrl { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? Address { get; set; }
    public string? Birthday { get; set; }
    public string? LinkedinUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public List<SocialAccountDto> SocialAccounts { get; set; } = [];
    public List<ContactInfoDto> ContactInfos { get; set; } = [];
}

// ---------------------------------------------------------------- Analytics / audit / import

public record CountDto(int Count, int TotalScans);
public record ChartPointDto(string Date, int Count);
public record RankingDto(Guid EmployeeId, string FullName, string? JobTitle, string? PhotoUrl, int ScanCount);

public class ScanLogDto
{
    public Guid Id { get; set; }
    /// <summary>Server receipt time in UTC (ISO-8601 with Z).</summary>
    public DateTime ScannedAt { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? PhotoUrl { get; set; }
    public string Source { get; set; } = "";
}

public class AuditLogDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public System.Text.Json.JsonElement? OldValues { get; set; }
    public System.Text.Json.JsonElement? NewValues { get; set; }
}

public record GeneratedCredentialDto(int Row, string Email, string Password);

public class ImportResultDto
{
    public int Created { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = [];
    /// <summary>Temporary passwords generated for rows that had no password column value.</summary>
    public List<GeneratedCredentialDto> GeneratedCredentials { get; set; } = [];
}
