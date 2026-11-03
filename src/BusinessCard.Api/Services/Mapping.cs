using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BusinessCard.Api.Services;

public static class Mapping
{
    public static UserDto ToDto(this User u) => Fill(new UserDto(), u);

    public static T Fill<T>(T d, User u) where T : UserDto
    {
        d.Id = u.Id;
        d.CompanyId = u.CompanyId;
        d.CompanyName = u.Company?.Name;
        d.CompanyLogoUrl = u.Company?.LogoUrl;
        d.BrandColor = u.Company is null ? null
            : string.IsNullOrEmpty(u.Company.BrandColor) ? Company.DefaultBrandColor : u.Company.BrandColor;
        d.FirstName = u.FirstName;
        d.LastName = u.LastName;
        d.MiddleName = u.MiddleName;
        d.FullName = u.FullName;
        d.JobTitle = u.JobTitle;
        d.Phone1 = u.Phone1;
        d.Phone2 = u.Phone2;
        d.WhatsappPhone = u.WhatsappPhone;
        d.ExtensionNumber = u.ExtensionNumber;
        d.AdditionalInfo = u.AdditionalInfo;
        d.Email = u.Email;
        d.Role = u.Role;
        d.IsActive = u.IsActive;
        d.CanEdit = u.CanEdit;
        d.PhotoUrl = u.PhotoUrl;
        d.CardBackgroundUrl = u.CardBackgroundUrl;
        d.GoogleMapsUrl = u.GoogleMapsUrl;
        d.Address = u.Address;
        d.Birthday = u.Birthday;
        d.LinkedinUrl = u.LinkedinUrl;
        d.FacebookUrl = u.FacebookUrl;
        d.InstagramUrl = u.InstagramUrl;
        d.Nickname = u.Nickname;
        d.SocialAccounts = Socials(u);
        d.ContactInfos = Contacts(u);
        d.CreatedAt = u.CreatedAt;
        return d;
    }

    public static List<SocialAccountDto> Socials(User u) => u.SocialAccounts
        .OrderBy(s => s.DisplayOrder)
        .Select(s => new SocialAccountDto { PlatformName = s.PlatformName, ProfileUrl = s.ProfileUrl, IconUrl = s.IconUrl })
        .ToList();

    public static List<ContactInfoDto> Contacts(User u) => u.ContactInfos
        .OrderBy(c => c.DisplayOrder)
        .Select(c => new ContactInfoDto { ContactType = c.ContactType, Value = c.Value, Label = c.Label, DisplayOrder = c.DisplayOrder })
        .ToList();

    public static CompanyDto ToDto(this Company c, int usedCount, string? adminEmail) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Voen = c.Voen,
        LogoUrl = c.LogoUrl,
        Address = c.Address,
        Email = c.Email,
        Phone = c.Phone,
        UserLimit = c.UserLimit,
        UsedCount = usedCount,
        NfcBaseUrl = c.NfcBaseUrl,
        BrandColor = string.IsNullOrEmpty(c.BrandColor) ? Company.DefaultBrandColor : c.BrandColor,
        IsActive = c.IsActive,
        AdminEmail = adminEmail,
        CreatedAt = c.CreatedAt,
    };

    public static PublicCardDto ToPublicCard(this User u)
    {
        var c = u.Company!;
        var brand = string.IsNullOrEmpty(c.BrandColor) ? Company.DefaultBrandColor : c.BrandColor;
        return new PublicCardDto
        {
            Id = u.Id,
            Nickname = u.Nickname,
            CompanyId = c.Id,
            CompanyName = c.Name,
            CompanyLogoUrl = c.LogoUrl,
            BrandColor = brand,
            Company = new PublicCompanyDto(c.Id, c.Name, c.LogoUrl, brand, c.Address, c.Phone, c.Email),
            FirstName = u.FirstName,
            LastName = u.LastName,
            MiddleName = u.MiddleName,
            FullName = u.FullName,
            JobTitle = u.JobTitle,
            Phone1 = u.Phone1,
            Phone2 = u.Phone2,
            WhatsappPhone = u.WhatsappPhone,
            ExtensionNumber = u.ExtensionNumber,
            AdditionalInfo = u.AdditionalInfo,
            Email = u.Email,
            PhotoUrl = u.PhotoUrl,
            CardBackgroundUrl = u.CardBackgroundUrl,
            GoogleMapsUrl = u.GoogleMapsUrl,
            Address = u.Address,
            Birthday = u.Birthday,
            LinkedinUrl = u.LinkedinUrl,
            FacebookUrl = u.FacebookUrl,
            InstagramUrl = u.InstagramUrl,
            SocialAccounts = Socials(u),
            ContactInfos = Contacts(u),
        };
    }
}

public static class DbErrors
{
    public static bool IsUniqueViolation(DbUpdateException ex, string constraintName) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal);
}
