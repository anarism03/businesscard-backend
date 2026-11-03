using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace BusinessCard.Api.Infrastructure;

/// <summary>
/// Creates the first super admin from configuration (Seed:SuperAdminEmail / Seed:SuperAdminPassword) when the
/// database has none. Does nothing once a super admin exists, so the secret can be removed afterwards.
/// </summary>
public static class SuperAdminSeeder
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var email = config["Seed:SuperAdminEmail"];
        var password = config["Seed:SuperAdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var db = services.GetRequiredService<AppDbContext>();
        try
        {
            if (await db.Users.AnyAsync(u => u.Role == Role.SuperAdmin)) return;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Super admin seeding skipped: database is not ready ({Message}). Run 'dotnet ef database update' first.", ex.Message);
            return;
        }

        Validation.ValidatePassword(password);
        var admin = new User
        {
            CompanyId = null,
            Email = Validation.NormalizeEmail(email),
            Role = Role.SuperAdmin,
            FirstName = "Super",
            LastName = "Admin",
            IsActive = true,
            CanEdit = true,
            MustChangePassword = false,
        };
        admin.PasswordHash = services.GetRequiredService<PasswordService>().Hash(admin, password);
        db.AuditEnabled = false;
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        logger.LogInformation("Super admin {Email} created.", admin.Email);
    }
}
