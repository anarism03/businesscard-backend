using System.Text.Json;
using BusinessCard.Api.Common;
using BusinessCard.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;

namespace BusinessCard.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, AuditActor? actor = null) : DbContext(options)
{
    public const string NicknameIndexName = "IX_Users_Nickname";
    public const string EmailIndexName = "IX_Users_CompanyId_Email";
    public const string VoenIndexName = "IX_Companies_Voen";
    public const string RoleConstraintName = "CK_Users_Role";
    public const string RoleCompanyConstraintName = "CK_Users_RoleCompany";

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ContactInfo> ContactInfos => Set<ContactInfo>();
    public DbSet<SocialAccount> SocialAccounts => Set<SocialAccount>();
    public DbSet<ScanEvent> ScanEvents => Set<ScanEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<StoredImage> StoredImages => Set<StoredImage>();

    /// <summary>Set to false to suppress automatic audit rows (e.g. seeding).</summary>
    public bool AuditEnabled { get; set; } = true;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Company>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Voen).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Voen).IsUnique().HasDatabaseName(VoenIndexName);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.NfcBaseUrl).HasMaxLength(500);
            e.Property(x => x.BrandColor).HasMaxLength(7).IsRequired().HasDefaultValue(Company.DefaultBrandColor);
            e.ToTable(t => t.HasCheckConstraint("CK_Companies_BrandColor", "\"BrandColor\" ~ '^#[0-9a-f]{6}$'"));
            e.HasMany(x => x.Users).WithOne(x => x.Company).HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<User>(e =>
        {
            e.Ignore(x => x.FullName);
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.MiddleName).HasMaxLength(100);
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.Nickname).HasMaxLength(30);
            // A single unique index enforces global (cross-company) uniqueness, including inactive users.
            // PostgreSQL unique indexes treat NULLs as distinct, so any number of users may have no nickname.
            e.HasIndex(x => x.Nickname).IsUnique().HasDatabaseName(NicknameIndexName);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Users_Nickname",
                    "\"Nickname\" IS NULL OR \"Nickname\" ~ '^[a-z0-9][a-z0-9_-]{1,28}[a-z0-9]$'");
                // Only defined roles; super admins have no company, company roles always have one.
                t.HasCheckConstraint(RoleConstraintName, "\"Role\" IN (0, 1, 2)");
                t.HasCheckConstraint(RoleCompanyConstraintName,
                    "(\"Role\" = 0 AND \"CompanyId\" IS NULL) OR (\"Role\" IN (1, 2) AND \"CompanyId\" IS NOT NULL)");
            });
            // E-mail is unique per company; super admins (CompanyId NULL) are unique among themselves.
            e.HasIndex(x => new { x.CompanyId, x.Email }).IsUnique().AreNullsDistinct(false).HasDatabaseName(EmailIndexName);
            e.HasMany(x => x.ContactInfos).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.SocialAccounts).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ContactInfo>(e =>
        {
            e.Property(x => x.ContactType).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.DisplayOrder });
        });

        b.Entity<SocialAccount>(e =>
        {
            e.Property(x => x.PlatformName).HasMaxLength(300);
            e.HasIndex(x => new { x.UserId, x.DisplayOrder });
        });

        b.Entity<ScanEvent>(e =>
        {
            e.Property(x => x.ScannedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.ClientHash).HasMaxLength(64);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CompanyId, x.ScannedAt });
            e.HasIndex(x => new { x.UserId, x.ScannedAt });
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.OldValues).HasColumnType("jsonb");
            e.Property(x => x.NewValues).HasColumnType("jsonb");
            e.Property(x => x.Action).HasMaxLength(50);
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.HasIndex(x => new { x.CompanyId, x.CreatedAt });
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<StoredImage>(e =>
        {
            e.Property(x => x.Kind).HasConversion<int>();
            e.HasIndex(x => new { x.CompanyId, x.UserId, x.Kind });
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified) continue;
            switch (entry.Entity)
            {
                case Company c: c.UpdatedAt = now; break;
                case User u: u.UpdatedAt = now; break;
            }
        }

        if (AuditEnabled) AddAuditRows(now);
        return await base.SaveChangesAsync(cancellationToken);
    }

    // ---- Automatic audit trail -------------------------------------------------------------

    private static readonly HashSet<string> IgnoredProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(User.PasswordHash), nameof(User.UpdatedAt), nameof(User.CreatedAt),
        nameof(User.PasswordChangedAt), nameof(User.SessionsRevokedAt), nameof(User.MustChangePassword),
    };

    private void AddAuditRows(DateTime now)
    {
        var rows = new List<AuditLog>();
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not (Company or User or ContactInfo or SocialAccount)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var (oldValues, newValues) = Snapshot(entry);
            if (entry.State == EntityState.Modified && newValues.Count == 0) continue;

            var (entityType, entityId, companyId) = entry.Entity switch
            {
                Company c => ("Company", c.Id, (Guid?)c.Id),
                User u => ("User", u.Id, u.CompanyId),
                ContactInfo ci => ("User", ci.UserId, actor?.CompanyId),
                SocialAccount sa => ("User", sa.UserId, actor?.CompanyId),
                _ => ("", Guid.Empty, null),
            };

            var action = entry.State switch
            {
                EntityState.Added => entry.Entity is ContactInfo or SocialAccount ? "ADD" : "CREATE",
                EntityState.Deleted => entry.Entity is ContactInfo or SocialAccount ? "REMOVE" : "DELETE",
                _ => ActivationAction(entry) ?? "UPDATE",
            };

            if (entry.State == EntityState.Deleted) oldValues["isDeleted"] = false;
            if (entry.State == EntityState.Deleted) newValues["isDeleted"] = true;

            rows.Add(new AuditLog
            {
                CreatedAt = now,
                CompanyId = companyId ?? actor?.CompanyId,
                UserId = actor?.UserId,
                UserEmail = actor?.Email,
                UserName = actor?.Name,
                Action = action,
                EntityType = entityType,
                EntityId = entityId.ToString(),
                OldValues = oldValues.Count > 0 ? JsonSerializer.Serialize(oldValues, JsonSerializerOptions.Web) : null,
                NewValues = newValues.Count > 0 ? JsonSerializer.Serialize(newValues, JsonSerializerOptions.Web) : null,
            });
        }
        AuditLogs.AddRange(rows);
    }

    private static string? ActivationAction(EntityEntry entry)
    {
        var isActive = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "IsActive");
        if (isActive is not { IsModified: true }) return null;
        var changedOthers = entry.Properties.Any(p => p.IsModified && p.Metadata.Name is not ("IsActive" or "UpdatedAt" or "SessionsRevokedAt"));
        if (changedOthers) return null;
        return Equals(isActive.CurrentValue, true) ? "ACTIVATE" : "DEACTIVATE";
    }

    private static (Dictionary<string, object?> Old, Dictionary<string, object?> New) Snapshot(EntityEntry entry)
    {
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (IgnoredProperties.Contains(name)) continue;
            var key = char.ToLowerInvariant(name[0]) + name[1..];
            switch (entry.State)
            {
                case EntityState.Added:
                    if (p.CurrentValue is not null && !(p.CurrentValue is string s && s.Length == 0))
                        newValues[key] = Safe(p.CurrentValue);
                    break;
                case EntityState.Deleted:
                    oldValues[key] = Safe(p.OriginalValue);
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    oldValues[key] = Safe(p.OriginalValue);
                    newValues[key] = Safe(p.CurrentValue);
                    break;
            }
        }
        return (oldValues, newValues);
    }

    /// <summary>Keeps audit rows small: inline images are recorded as a marker, never as their bytes.</summary>
    private static object? Safe(object? value) => value switch
    {
        string s when s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) => "data:image/*",
        string s when s.Length > 2000 => s[..2000] + "…",
        Role r => (int)r,
        _ => value,
    };
}

/// <summary>Used by <c>dotnet ef</c> at design time.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets<DesignTimeDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("DefaultConnection")
                 ?? "Host=localhost;Port=5432;Database=businesscard_db;Username=businesscard_app;Password=design-time-only";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        return new AppDbContext(options);
    }
}
