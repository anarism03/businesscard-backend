using System.Security.Cryptography;
using System.Text;
using BusinessCard.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BusinessCard.Api.Services;

public class JwtOptions
{
    public string Issuer { get; set; } = "BusinessCard.Api";
    public string Audience { get; set; } = "BusinessCard.Frontend";
    /// <summary>HMAC signing key, at least 32 characters. Keep it in user-secrets / environment, never in git.</summary>
    public string Key { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 480;
}

public class AuthOptions
{
    /// <summary>Days a password stays valid. 0 disables expiry.</summary>
    public int PasswordExpiryDays { get; set; } = 90;
}

public class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
{
    private readonly JwtOptions _o = options.Value;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(_o.Key));

    public (string Token, DateTime ExpiresAt) Create(User user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_o.ExpiryMinutes);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["email"] = user.Email,
            ["name"] = user.FullName,
            ["given_name"] = user.FirstName,
            ["family_name"] = user.LastName,
            ["role"] = user.Role.ToString(),
            ["CanEdit"] = user.CanEdit,
            ["isFirstLogin"] = user.MustChangePassword,
            ["iat"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
        };
        if (user.CompanyId is { } companyId) claims["CompanyId"] = companyId.ToString();

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}

public class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(User user, string password)
    {
        if (string.IsNullOrEmpty(user.PasswordHash)) return false;
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Random temporary password with upper, lower, digit and symbol characters.</summary>
    public static string Generate(int length = 12)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*?";
        const string all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)],
        };
        while (chars.Count < length) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
