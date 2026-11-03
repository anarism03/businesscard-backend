using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusinessCard.Api.Domain;

namespace BusinessCard.Api.Services;

/// <summary>One link exactly as the public card shows it.</summary>
public sealed record VisibleLinkRow(string Id, string? Platform, string Category, string Label, string Value, string Href);

/// <summary>Settings of the "save contact" button stored as <c>__setclapp_contact_card__:*</c> metadata rows.</summary>
public sealed record ContactCardSettings(string ButtonLabel, string Name, string Website, bool Enabled)
{
    public const string DefaultButtonLabel = "Kontaktı Yüklə";
    public bool HasFields => !string.IsNullOrWhiteSpace(ButtonLabel) || !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Website);
    /// <summary>The card shows the button (and its name/website override the vCard) only when this is true.</summary>
    public bool Active => HasFields && Enabled;
}

/// <summary>
/// Server-side port of the frontend's visible-card model (<c>buildViewLinkRows</c>, <c>getDisabledLinkKeys</c>,
/// contact-card fields). VCF and offline HTML exports are built only from this model, so hidden links and
/// <c>__setclapp_*</c> metadata never leave the server and every export path shows the same data as the card.
/// </summary>
public sealed partial class VisibleCard
{
    public const string MetadataLabelPrefix = "__setclapp_";
    public const string DisabledLinksPrefix = "__setclapp_disabled_links__:";
    public const string ContactCardLabelPrefix = "__setclapp_contact_card__:";
    public const string LegacyContactCardTypePrefix = "contact-card:";
    private const string CustomWebsitePrefix = "website::";

    public required User User { get; init; }
    public required IReadOnlyList<VisibleLinkRow> Rows { get; init; }
    public required ContactCardSettings ContactCard { get; init; }
    public required IReadOnlySet<string> HiddenKeys { get; init; }

    /// <summary>The address is part of the card through the Google Maps row; hiding that row hides it too.</summary>
    public string? VisibleAddress =>
        HiddenKeys.Contains("core:googleMapsUrl") || string.IsNullOrWhiteSpace(User.Address) ? null : User.Address.Trim();

    // ------------------------------------------------------------------ link type metadata (constants/linkTypes.tsx)

    private sealed record LinkMeta(string Title, string Kind, string Category, string? UrlPrefix = null);

    private static readonly Dictionary<string, LinkMeta> LinkTypes = new()
    {
        ["contact-card"] = new("Kontakt kartı", "url", "contact"),
        ["phone"] = new("Telefon", "phone", "contact"),
        ["email"] = new("Email", "email", "contact"),
        ["messenger"] = new("Messenger", "url", "contact"),
        ["whatsapp"] = new("WhatsApp", "phone", "contact"),
        ["whatsapp-business"] = new("WhatsApp Business", "phone", "contact"),
        ["sms"] = new("SMS", "phone", "contact"),
        ["telegram"] = new("Telegram", "url", "social"),
        ["viber"] = new("Viber", "phone", "contact"),
        ["zoom"] = new("Zoom", "url", "contact"),
        ["skype"] = new("Skype", "url", "contact"),
        ["facebook"] = new("Facebook", "url", "social"),
        ["instagram"] = new("Instagram", "username", "social", "https://www.instagram.com/"),
        ["linkedin"] = new("LinkedIn", "url", "social"),
        ["tiktok"] = new("TikTok", "username", "social", "https://www.tiktok.com/@"),
        ["youtube"] = new("YouTube", "url", "social"),
        ["snapchat"] = new("Snapchat", "url", "social"),
        ["threads"] = new("Threads", "url", "social"),
        ["twitch"] = new("Twitch", "url", "social"),
        ["vk"] = new("VK", "url", "social"),
        ["wechat"] = new("WeChat", "url", "social"),
        ["x"] = new("X (Twitter)", "url", "social"),
        ["github"] = new("GitHub", "url", "social"),
        ["discord"] = new("Discord", "url", "social"),
        ["slack"] = new("Slack", "url", "social"),
        ["spotify"] = new("Spotify", "url", "music"),
        ["applemusic"] = new("Apple Music", "url", "music"),
        ["youtube-music"] = new("YouTube Music", "url", "music"),
        ["googlemaps"] = new("Xəritələr", "url", "business"),
        ["amazon"] = new("Amazon", "url", "business"),
        ["lalafo"] = new("Lalafo", "url", "business"),
        ["tapaz"] = new("Tap.az", "url", "business"),
        ["website"] = new("Sayt linki", "url", "other"),
    };

    private static readonly LinkMeta Fallback = new("Keçid", "url", "other");

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["tel"] = "phone", ["telephone"] = "phone", ["mobile"] = "phone", ["mobilephone"] = "phone",
        ["mail"] = "email", ["twitter"] = "x", ["xtwitter"] = "x",
        ["web"] = "website", ["url"] = "website", ["link"] = "website",
    };

    private static readonly Dictionary<string, string> TypesByToken =
        LinkTypes.Keys.ToDictionary(Token, k => k);

    private static string Token(string value)
    {
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        return new string(normalized.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
    }

    private static string? ResolveType(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var token = Token(candidate);
            if (Aliases.TryGetValue(token, out var alias)) return alias;
            if (TypesByToken.TryGetValue(token, out var type)) return type;
        }
        return null;
    }

    // ------------------------------------------------------------------ build

    public static VisibleCard Build(User user)
    {
        var contacts = user.ContactInfos.OrderBy(c => c.DisplayOrder).ToList();
        var hidden = ReadHiddenKeys(contacts);
        var rows = new List<VisibleLinkRow>();

        void AddCore(string key, string label, string? value, string? platform, string category, Func<string, string> href,
            Func<string, string>? display = null)
        {
            if (string.IsNullOrEmpty(value) || hidden.Contains($"core:{key}")) return;
            rows.Add(new VisibleLinkRow($"core:{key}", platform, category, label, display?.Invoke(value) ?? value, Sanitize(href(value))));
        }

        AddCore("phone1", "Telefon", user.Phone1, "phone", "contact", v => $"tel:{v}");
        AddCore("phone2", "Şəxsi telefon", user.Phone2, "phone", "contact", v => $"tel:{v}");
        AddCore("whatsappPhone", "WhatsApp", user.WhatsappPhone, "whatsapp", "contact", WhatsAppLink);
        AddCore("linkedinUrl", "LinkedIn", user.LinkedinUrl, "linkedin", "social", v => v);
        AddCore("facebookUrl", "Facebook", user.FacebookUrl, "facebook", "social", v => v);
        AddCore("instagramUrl", "Instagram", user.InstagramUrl, "instagram", "social", v => v,
            v => UrlToUsername("https://www.instagram.com/", v));
        AddCore("googleMapsUrl", string.IsNullOrWhiteSpace(user.Address) ? "Google Maps" : user.Address.Trim(),
            user.GoogleMapsUrl, "googlemaps", "business", v => v);

        if (!string.IsNullOrEmpty(user.Email) && !hidden.Contains("core:email"))
            rows.Add(new VisibleLinkRow("core:email", "email", "contact", "E-poçt", user.Email, Sanitize($"mailto:{user.Email}")));

        for (var i = 0; i < contacts.Count; i++)
        {
            var info = contacts[i];
            if (string.IsNullOrEmpty(info.Value) || IsMetadata(info) || hidden.Contains($"contact:{info.ContactType}:{info.Value}"))
                continue;
            var type = ResolveType(info.Label, info.ContactType);
            var meta = type is null ? Fallback : LinkTypes[type];
            rows.Add(new VisibleLinkRow(
                $"contact:{info.ContactType}:{i}",
                type ?? info.ContactType.ToLowerInvariant(),
                meta.Category,
                string.IsNullOrEmpty(info.Label) ? meta.Title : info.Label,
                info.Value,
                Sanitize(ContactHref(info.Value, type, meta))));
        }

        var socials = user.SocialAccounts.OrderBy(s => s.DisplayOrder).ToList();
        for (var i = 0; i < socials.Count; i++)
        {
            var account = socials[i];
            if (string.IsNullOrEmpty(account.ProfileUrl) || hidden.Contains($"social:{account.PlatformName}:{account.ProfileUrl}"))
                continue;
            var headline = WebsiteHeadline(account.PlatformName);
            var type = headline is null ? ResolveType(account.PlatformName) : null;
            var meta = headline is not null ? LinkTypes["website"] : type is null ? Fallback : LinkTypes[type];
            rows.Add(new VisibleLinkRow(
                $"social:{account.PlatformName}:{i}",
                type ?? account.PlatformName.ToLowerInvariant(),
                meta.Category,
                headline ?? meta.Title,
                account.ProfileUrl,
                Sanitize(account.ProfileUrl)));
        }

        return new VisibleCard
        {
            User = user,
            Rows = Dedupe(rows),
            ContactCard = ReadContactCard(contacts),
            HiddenKeys = hidden,
        };
    }

    public static bool IsMetadata(ContactInfo info) =>
        (info.Label?.StartsWith(MetadataLabelPrefix, StringComparison.Ordinal) ?? false) ||
        info.ContactType.StartsWith(LegacyContactCardTypePrefix, StringComparison.Ordinal);

    private static HashSet<string> ReadHiddenKeys(IEnumerable<ContactInfo> contacts)
    {
        var marker = contacts.FirstOrDefault(c => c.Label?.StartsWith(DisabledLinksPrefix, StringComparison.Ordinal) == true);
        if (marker is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(marker.Value);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ContactCardSettings ReadContactCard(IEnumerable<ContactInfo> contacts)
    {
        string buttonLabel = "", name = "", website = "";
        var enabled = true;
        foreach (var info in contacts)
        {
            var key = info.ContactType.StartsWith(LegacyContactCardTypePrefix, StringComparison.Ordinal)
                ? info.ContactType[LegacyContactCardTypePrefix.Length..]
                : info.Label?.StartsWith(ContactCardLabelPrefix, StringComparison.Ordinal) == true
                    ? info.Label[ContactCardLabelPrefix.Length..]
                    : null;
            switch (key)
            {
                case "buttonLabel": buttonLabel = info.Value; break;
                case "name": name = info.Value; break;
                case "website": website = info.Value; break;
                case "enabled": enabled = !string.Equals(info.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        if (!string.IsNullOrWhiteSpace(buttonLabel) && KnownSaveLabel().IsMatch(buttonLabel.Trim()))
            buttonLabel = ContactCardSettings.DefaultButtonLabel;
        return new ContactCardSettings(buttonLabel.Trim(), name.Trim(), website.Trim(), enabled);
    }

    [GeneratedRegex(@"^kontakt[iı]\s+yadda\s+saxla+$", RegexOptions.IgnoreCase)]
    private static partial Regex KnownSaveLabel();

    private static string? WebsiteHeadline(string platformName)
    {
        if (!platformName.StartsWith(CustomWebsitePrefix, StringComparison.Ordinal)) return null;
        var headline = platformName[CustomWebsitePrefix.Length..].Trim();
        return headline.Length == 0 ? "Sayt linki" : headline;
    }

    private static List<VisibleLinkRow> Dedupe(List<VisibleLinkRow> rows)
    {
        var seen = new HashSet<string>();
        return rows.Where(row =>
        {
            if (row.Category == "other" || string.IsNullOrEmpty(row.Platform)) return true;
            return seen.Add($"{row.Platform}::{row.Href.Trim().ToLowerInvariant().TrimEnd('/')}");
        }).ToList();
    }

    // ------------------------------------------------------------------ hrefs (utils/url.ts, cardLinkRows.tsx)

    public static string Digits(string? value) => new((value ?? "").Where(char.IsAsciiDigit).ToArray());

    private static string NormalizePhone(string value)
    {
        var digits = Digits(value);
        return digits.Length == 0 ? "" : ("+" + digits)[..Math.Min(30, digits.Length + 1)];
    }

    private static string WhatsAppLink(string phone) => $"https://wa.me/{Digits(NormalizePhone(phone))}";

    private static string ExtractUsername(string raw) =>
        Regex.Replace(Regex.Replace(raw.Trim(), @"^https?://(www\.)?[^/]+/", "", RegexOptions.IgnoreCase), "^@", "").TrimEnd('/').Trim();

    private static string DomainLabel(string url) =>
        Regex.Replace(Regex.Replace(url, "^https?://", "", RegexOptions.IgnoreCase), "^www\\.", "", RegexOptions.IgnoreCase);

    private static string UrlToUsername(string prefix, string url)
    {
        var host = DomainLabel(url);
        var prefixHost = DomainLabel(prefix);
        return host.StartsWith(prefixHost, StringComparison.OrdinalIgnoreCase) ? host[prefixHost.Length..].TrimEnd('/') : url;
    }

    private static string ContactHref(string value, string? type, LinkMeta meta) => type switch
    {
        "email" => $"mailto:{value}",
        "whatsapp" or "whatsapp-business" => WhatsAppLink(value),
        "sms" => $"sms:{NormalizePhone(value)}",
        "viber" => $"viber://chat?number={Uri.EscapeDataString(NormalizePhone(value))}",
        _ when meta.Kind == "username" && meta.UrlPrefix is not null => meta.UrlPrefix + ExtractUsername(value),
        _ => meta.Kind == "phone" ? $"tel:{value}" : value,
    };

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex HasScheme();

    [GeneratedRegex("^(https?:|mailto:|tel:|sms:|viber:)", RegexOptions.IgnoreCase)]
    private static partial Regex SafeScheme();

    /// <summary>Drops javascript:, data: and other unsafe schemes exactly like the frontend.</summary>
    public static string Sanitize(string href)
    {
        var trimmed = href.Trim();
        if (trimmed.Length == 0) return "";
        var compact = new string(trimmed.Where(c => c > ' ' && c != (char)127).ToArray());
        return HasScheme().IsMatch(compact) && !SafeScheme().IsMatch(compact) ? "" : trimmed;
    }

    // ------------------------------------------------------------------ vCard (utils/vcard.ts buildVcfText)

    private static readonly HashSet<string> LabeledPhonePlatforms = ["whatsapp", "whatsapp-business", "viber", "sms"];

    public static string EscapeVcf(string? value) => (value ?? "").Trim()
        .Replace("\\", "\\\\")
        .Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n")
        .Replace(",", "\\,")
        .Replace(";", "\\;");

    /// <summary>RFC 6350 line folding at 75 octets (UTF-8 aware, never splits a character).</summary>
    public static string FoldLine(string line)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 75)
            {
                parts.Add(part.ToString());
                part.Clear().Append(' ');
                bytes = 1;
            }
            part.Append(rune.ToString());
            bytes += size;
        }
        parts.Add(part.ToString());
        return string.Join("\r\n", parts);
    }

    /// <param name="photo">Optional JPEG/PNG bytes already prepared for embedding.</param>
    public string BuildVcf((byte[] Bytes, string Type)? photo = null)
    {
        var u = User;
        var overrides = ContactCard.Active ? ContactCard : null;
        var fullName = !string.IsNullOrWhiteSpace(overrides?.Name) ? overrides!.Name : u.FullName;
        var lines = new List<string>
        {
            "BEGIN:VCARD",
            "VERSION:3.0",
            !string.IsNullOrWhiteSpace(overrides?.Name)
                ? $"N:{EscapeVcf(overrides!.Name)};;;;"
                : $"N:{EscapeVcf(u.LastName)};{EscapeVcf(u.FirstName)};;;",
            $"FN:{EscapeVcf(fullName)}",
        };
        if (!string.IsNullOrWhiteSpace(u.Company?.Name)) lines.Add($"ORG:{EscapeVcf(u.Company!.Name)}");
        if (!string.IsNullOrWhiteSpace(u.JobTitle))
        {
            lines.Add($"TITLE:{EscapeVcf(u.JobTitle)}");
            lines.Add($"ROLE:{EscapeVcf(u.JobTitle)}");
        }
        var birthday = (u.Birthday ?? "").Trim();
        if (birthday.Length >= 10 && DateOnly.TryParseExact(birthday[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            lines.Add($"BDAY:{birthday[..10]}");

        var item = 0;
        var seen = new HashSet<string>();
        void Push(string property, string key, string? label)
        {
            if (!seen.Add(key)) return;
            if (string.IsNullOrEmpty(label)) { lines.Add(property); return; }
            item++;
            lines.Add($"item{item}.{property}");
            lines.Add($"item{item}.X-ABLabel:{EscapeVcf(label)}");
        }
        void PushTel(string value, string types, string? label = null)
        {
            var digits = Digits(value);
            if (digits.Length == 0) return;
            Push($"TEL;TYPE={types}:{EscapeVcf(value)}", $"tel:{label ?? ""}:{digits[^Math.Min(9, digits.Length)..]}", label);
        }
        void PushEmail(string value, string? label = null)
        {
            var key = value.Trim().ToLowerInvariant();
            if (key.Length > 0) Push($"EMAIL;TYPE=INTERNET:{EscapeVcf(value)}", $"email:{key}", label);
        }
        void PushUrl(string href, string? label = null)
        {
            var value = href.Trim().Replace("\r", "").Replace("\n", "");
            var key = value.ToLowerInvariant().TrimEnd('/');
            if (key.Length > 0) Push($"URL:{value}", $"url:{key}", label);
        }

        if (!string.IsNullOrEmpty(u.Phone1) && !HiddenKeys.Contains("core:phone1")) PushTel(u.Phone1, "WORK,VOICE");
        if (!string.IsNullOrEmpty(u.Phone2) && !HiddenKeys.Contains("core:phone2")) PushTel(u.Phone2, "CELL,VOICE");
        if (!string.IsNullOrEmpty(u.Email) && !HiddenKeys.Contains("core:email")) PushEmail(u.Email);
        if (VisibleAddress is { } address)
        {
            lines.Add($"ADR;TYPE=WORK:;;{EscapeVcf(address)};;;;");
            lines.Add($"LABEL;TYPE=WORK:{EscapeVcf(address)}");
        }
        if (!string.IsNullOrWhiteSpace(overrides?.Website)) PushUrl(overrides!.Website, "Vebsayt");

        foreach (var row in Rows)
        {
            if (row.Id is "core:phone1" or "core:phone2" or "core:email") continue;
            var platform = row.Platform ?? "";
            if (platform is "contact-card" or "googlemaps") continue;
            if (platform == "phone") { PushTel(row.Value, "VOICE", row.Label); continue; }
            if (LabeledPhonePlatforms.Contains(platform)) { PushTel(row.Value, "CELL,VOICE", row.Label); continue; }
            if (platform == "email") { PushEmail(row.Value, row.Label); continue; }
            if (!row.Href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !row.Href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            PushUrl(row.Href, row.Label);
        }

        if (!string.IsNullOrWhiteSpace(u.AdditionalInfo)) lines.Add($"NOTE:{EscapeVcf(u.AdditionalInfo)}");
        if (photo is { } p) lines.Add($"PHOTO;ENCODING=b;TYPE={p.Type}:{Convert.ToBase64String(p.Bytes)}");
        lines.Add("END:VCARD");
        return string.Join("\r\n", lines.Select(FoldLine)) + "\r\n";
    }
}
