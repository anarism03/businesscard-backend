using System.Net.Mail;
using System.Text.RegularExpressions;
using BusinessCard.Api.Common;
using BusinessCard.Api.Domain;

namespace BusinessCard.Api.Services;

public static partial class Validation
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex BrandColorRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9]$")]
    private static partial Regex NicknameRegex();

    public static string NormalizeEmail(string? email)
    {
        var value = (email ?? "").Trim().ToLowerInvariant();
        if (value.Length is 0 or > 256 || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
            throw ApiException.BadRequest("INVALID_EMAIL", "Email address is invalid");
        return value;
    }

    /// <summary>VÖEN: exactly 10 digits.</summary>
    public static string NormalizeVoen(string? voen)
    {
        var value = (voen ?? "").Trim();
        if (value.Length == 0) throw ApiException.BadRequest("VOEN_REQUIRED", "VOEN is required");
        if (value.Length != 10 || !value.All(char.IsAsciiDigit))
            throw ApiException.BadRequest("VOEN_INVALID", "VOEN must contain exactly 10 digits");
        return value;
    }

    /// <summary>Strict #RRGGBB; returned lower-case. Null means "use the default".</summary>
    public static string NormalizeBrandColor(string? color)
    {
        if (color is null) return Company.DefaultBrandColor;
        var value = color.Trim();
        if (!BrandColorRegex().IsMatch(value))
            throw ApiException.BadRequest("INVALID_BRAND_COLOR", "brandColor must be a HEX color in #RRGGBB format");
        return value.ToLowerInvariant();
    }

    /// <summary>Trim + invariant lower-case. Null/empty/whitespace → null (removes the nickname).</summary>
    public static string? NormalizeNickname(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        var value = nickname.Trim().ToLowerInvariant();
        if (!NicknameRegex().IsMatch(value))
            throw ApiException.BadRequest("INVALID_NICKNAME", "İstifadəçi adının formatı düzgün deyil.");
        return value;
    }

    /// <summary>Only the defined roles (0, 1, 2) are accepted; any other number is a 400.</summary>
    public static Role RequireDefinedRole(Role role)
    {
        if (!Enum.IsDefined(role))
            throw ApiException.BadRequest("INVALID_ROLE", "Role must be one of: 0 (SuperAdmin), 1 (CompanyAdmin), 2 (Employee)");
        return role;
    }

    /// <summary>A stored account is consistent when super admins have no company and company roles have one.</summary>
    public static bool HasValidRoleAndCompany(User user) => user.Role switch
    {
        Role.SuperAdmin => user.CompanyId is null,
        Role.CompanyAdmin or Role.Employee => user.CompanyId is not null,
        _ => false,
    };

    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw ApiException.BadRequest("INVALID_PASSWORD", "Password must be at least 6 characters");
        if (password.Length > 128)
            throw ApiException.BadRequest("INVALID_PASSWORD", "Password is too long");
    }

    public static string RequireText(string? value, string field, int max = 200)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) throw ApiException.BadRequest("VALIDATION_ERROR", $"{field} is required");
        if (text.Length > max) throw ApiException.BadRequest("VALIDATION_ERROR", $"{field} is too long");
        return text;
    }

    /// <summary>Null keeps the current value; empty string clears it.</summary>
    public static string? Patch(string? incoming, string? current) =>
        incoming is null ? current : (incoming.Trim().Length == 0 ? null : incoming.Trim());
}
