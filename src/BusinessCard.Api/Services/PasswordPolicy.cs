using BusinessCard.Api.Domain;

namespace BusinessCard.Api.Services;

/// <summary>
/// Server-side password rules shared by account-info and request authorization, so the frontend's
/// "days until expiry" and the backend's block always agree.
/// </summary>
public static class PasswordPolicy
{
    public const string ChangeRequiredCode = "PASSWORD_CHANGE_REQUIRED";
    public const string ExpiredCode = "PASSWORD_EXPIRED";

    /// <summary>Whole days left before the password expires; null when expiry is disabled (0 days).</summary>
    public static int? DaysUntilExpiry(User user, AuthOptions options, DateTime nowUtc)
    {
        if (options.PasswordExpiryDays <= 0) return null;
        var expiresAt = user.PasswordChangedAt.AddDays(options.PasswordExpiryDays);
        return (int)Math.Floor((expiresAt - nowUtc).TotalDays);
    }

    public static DateTime? ExpiresAt(User user, AuthOptions options) =>
        options.PasswordExpiryDays <= 0 ? null : user.PasswordChangedAt.AddDays(options.PasswordExpiryDays);

    /// <summary>The error code that blocks business requests, or null when the password is usable.</summary>
    public static string? BlockingCode(User user, AuthOptions options, DateTime nowUtc)
    {
        if (user.MustChangePassword) return ChangeRequiredCode;
        return DaysUntilExpiry(user, options, nowUtc) is <= 0 ? ExpiredCode : null;
    }
}
