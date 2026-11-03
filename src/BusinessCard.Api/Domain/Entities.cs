namespace BusinessCard.Api.Domain;

public enum Role
{
    SuperAdmin = 0,
    CompanyAdmin = 1,
    Employee = 2,
}

public enum ImageKind
{
    ProfilePhoto = 0,
    CardBackground = 1,
    CompanyLogo = 2,
}

public class Company
{
    public const string DefaultBrandColor = "#1b4a75";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Voen { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string? Address { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int UserLimit { get; set; }
    public string? NfcBaseUrl { get; set; }
    public string BrandColor { get; set; } = DefaultBrandColor;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<User> Users { get; set; } = [];
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string? JobTitle { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }
    public string? WhatsappPhone { get; set; }
    public string? ExtensionNumber { get; set; }
    public string? AdditionalInfo { get; set; }

    /// <summary>Stored lower-case. Login identity together with the company VÖEN.</summary>
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Role Role { get; set; } = Role.Employee;
    public bool IsActive { get; set; } = true;
    public bool CanEdit { get; set; }
    public bool MustChangePassword { get; set; } = true;
    public DateTime PasswordChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Tokens issued before this moment are rejected (set by admin password reset / deactivation).</summary>
    public DateTime? SessionsRevokedAt { get; set; }

    public string? PhotoUrl { get; set; }
    public string? CardBackgroundUrl { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public string? Address { get; set; }
    public string? Birthday { get; set; }
    public string? LinkedinUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }

    /// <summary>Optional, globally unique, normalized lower-case public alias. Never replaces <see cref="Id"/>.</summary>
    public string? Nickname { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ContactInfo> ContactInfos { get; set; } = [];
    public List<SocialAccount> SocialAccounts { get; set; } = [];

    public string FullName =>
        string.Join(" ", new[] { FirstName, LastName, MiddleName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public class ContactInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ContactType { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    public int DisplayOrder { get; set; }
}

public class SocialAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string PlatformName { get; set; } = "";
    public string ProfileUrl { get; set; } = "";
    public string? IconUrl { get; set; }
    public int DisplayOrder { get; set; }
}

public class ScanEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Server-generated UTC time at request receipt (timestamptz).</summary>
    public DateTime ScannedAt { get; set; }
    public string Source { get; set; } = "link";
    public string? ClientHash { get; set; }
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
}

/// <summary>An uploaded image: the private original plus the current public cropped result.</summary>
public class StoredImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ImageKind Kind { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? UserId { get; set; }

    public string OriginalPath { get; set; } = "";
    public string OriginalContentType { get; set; } = "";
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }

    public string ResultPath { get; set; } = "";
    public string ResultUrl { get; set; } = "";
    public int ResultWidth { get; set; }
    public int ResultHeight { get; set; }

    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }

    public Guid? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
