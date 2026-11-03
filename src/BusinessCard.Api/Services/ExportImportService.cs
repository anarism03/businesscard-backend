using System.Data;
using System.IO.Compression;
using System.Net;
using System.Text;
using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using BusinessCard.Api.Dtos;
using ClosedXML.Excel;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BusinessCard.Api.Services;

public class AppOptions
{
    /// <summary>Base of public card links, e.g. https://card.example.az/v/ — the user UUID is appended.</summary>
    public string PublicCardBaseUrl { get; set; } = "http://127.0.0.1:5173/v/";
}

public record FileResultData(byte[] Content, string ContentType, string FileName);

public class ExportImportService(
    AppDbContext db,
    UserService users,
    PasswordService passwords,
    IOptions<StorageOptions> storageOptions,
    IWebHostEnvironment env)
{
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Excel columns: (property key, Azerbaijani header). Import accepts either header text or key.</summary>
    private static readonly (string Key, string Header)[] Columns =
    [
        ("firstName", "Ad"), ("lastName", "Soyad"), ("middleName", "Ata adı"), ("jobTitle", "Vəzifə"),
        ("email", "E-poçt"), ("password", "Şifrə"), ("phone1", "Telefon 1"), ("phone2", "Telefon 2"),
        ("whatsappPhone", "WhatsApp"), ("extensionNumber", "Daxili nömrə"), ("address", "Ünvan"),
        ("birthday", "Doğum tarixi"), ("linkedinUrl", "LinkedIn"), ("facebookUrl", "Facebook"),
        ("instagramUrl", "Instagram"), ("googleMapsUrl", "Google Maps"), ("additionalInfo", "Əlavə məlumat"),
    ];

    // ------------------------------------------------------------------ access helpers

    private async Task<List<User>> LoadUsersAsync(User caller, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) throw ApiException.BadRequest("VALIDATION_ERROR", "Select at least one user");
        if (ids.Count > 5000) throw ApiException.BadRequest("VALIDATION_ERROR", "Too many users selected");
        var list = await users.WithDetails().Where(u => ids.Contains(u.Id)).ToListAsync();
        if (list.Count != ids.Distinct().Count()) throw ApiException.NotFound("One or more users were not found");
        foreach (var u in list) UserService.EnsureCanView(caller, u);
        return list.OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ToList();
    }

    private async Task<List<User>> LoadCompanyUsersAsync(User caller, Guid companyId)
    {
        CurrentUser.EnsureCompanyAccess(caller, companyId);
        if (!await db.Companies.AnyAsync(c => c.Id == companyId)) throw ApiException.NotFound("Company not found");
        return await users.WithDetails().Where(u => u.CompanyId == companyId)
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ToListAsync();
    }

    private void Audit(User caller, string action, string entityType, string? entityId, object? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = caller.CompanyId, UserId = caller.Id, UserEmail = caller.Email, UserName = caller.FullName,
            Action = action, EntityType = entityType, EntityId = entityId,
            NewValues = details is null ? null : System.Text.Json.JsonSerializer.Serialize(details, System.Text.Json.JsonSerializerOptions.Web),
        });
    }

    // ------------------------------------------------------------------ VCF

    public async Task<FileResultData> VcfAsync(User caller, Guid userId)
    {
        var user = await users.GetDetailedAsync(userId);
        UserService.EnsureCanView(caller, user);
        var card = VisibleCard.Build(user);
        var vcf = card.BuildVcf(await VcfPhotoAsync(user.PhotoUrl));
        return new FileResultData(Encoding.UTF8.GetBytes(vcf), "text/vcard; charset=utf-8", $"{SafeName(user.FullName)}.vcf");
    }

    /// <summary>Photo for the vCard: JPEG, at most 600px, like the frontend's download button.</summary>
    private async Task<(byte[] Bytes, string Type)?> VcfPhotoAsync(string? url)
    {
        var data = await ImageAsDataAsync(url);
        if (data is null) return null;
        try
        {
            using var oriented = ImageService.DecodeOriented(data.Value.Bytes, storageOptions.Value.MaxSourcePixels);
            var scale = Math.Min(1.0, 600.0 / Math.Max(oriented.Width, oriented.Height));
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(oriented.Width * scale)),
                Math.Max(1, (int)Math.Round(oriented.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            surface.Canvas.Clear(SKColors.White);
            using (var image = SKImage.FromBitmap(oriented))
                surface.Canvas.DrawImage(image, new SKRect(0, 0, oriented.Width, oriented.Height),
                    new SKRect(0, 0, info.Width, info.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
            return jpeg is null ? null : (jpeg.ToArray(), "JPEG");
        }
        catch (ApiException)
        {
            return null; // undecodable stored image: export the card without a photo
        }
    }

    // ------------------------------------------------------------------ Excel export

    public async Task<FileResultData> ExcelCompanyAsync(User caller, Guid companyId)
    {
        var list = await LoadCompanyUsersAsync(caller, companyId);
        Audit(caller, "EXPORT_EXCEL", "Company", companyId.ToString(), new { count = list.Count });
        await db.SaveChangesAsync();
        return new FileResultData(BuildWorkbook(list), XlsxMime, "employees.xlsx");
    }

    public async Task<FileResultData> ExcelSelectedAsync(User caller, List<Guid> ids)
    {
        var list = await LoadUsersAsync(caller, ids);
        Audit(caller, "EXPORT_EXCEL", "User", null, new { count = list.Count });
        await db.SaveChangesAsync();
        return new FileResultData(BuildWorkbook(list), XlsxMime, "employees-selected.xlsx");
    }

    public FileResultData Template()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Employees");
        WriteHeader(ws);
        var sample = new Dictionary<string, string>
        {
            ["firstName"] = "Anar", ["lastName"] = "Məmmədov", ["jobTitle"] = "Satış meneceri",
            ["email"] = "anar@example.az", ["phone1"] = "+994501234567",
        };
        for (var i = 0; i < Columns.Length; i++) ws.Cell(2, i + 1).Value = sample.GetValueOrDefault(Columns[i].Key, "");
        ws.Columns().AdjustToContents();
        return new FileResultData(Save(wb), XlsxMime, "employees-template.xlsx");
    }

    private static void WriteHeader(IXLWorksheet ws)
    {
        for (var i = 0; i < Columns.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Columns[i].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF5");
        }
        ws.SheetView.FreezeRows(1);
    }

    private static byte[] BuildWorkbook(List<User> list)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Employees");
        var exportColumns = Columns.Where(c => c.Key != "password").ToArray();
        for (var i = 0; i < exportColumns.Length; i++)
        {
            ws.Cell(1, i + 1).Value = exportColumns[i].Header;
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }
        var extra = exportColumns.Length;
        ws.Cell(1, extra + 1).Value = "Status";
        ws.Cell(1, extra + 2).Value = "Nickname";
        ws.Cell(1, extra + 3).Value = "ID";
        ws.Range(1, extra + 1, 1, extra + 3).Style.Font.Bold = true;

        var row = 2;
        foreach (var u in list)
        {
            for (var i = 0; i < exportColumns.Length; i++)
                ws.Cell(row, i + 1).Value = FieldValue(u, exportColumns[i].Key) ?? "";
            ws.Cell(row, extra + 1).Value = u.IsActive ? "Aktiv" : "Deaktiv";
            ws.Cell(row, extra + 2).Value = u.Nickname ?? "";
            ws.Cell(row, extra + 3).Value = u.Id.ToString();
            row++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(row, 200));
        return Save(wb);
    }

    private static string? FieldValue(User u, string key) => key switch
    {
        "firstName" => u.FirstName, "lastName" => u.LastName, "middleName" => u.MiddleName,
        "jobTitle" => u.JobTitle, "email" => u.Email, "phone1" => u.Phone1, "phone2" => u.Phone2,
        "whatsappPhone" => u.WhatsappPhone, "extensionNumber" => u.ExtensionNumber, "address" => u.Address,
        "birthday" => u.Birthday, "linkedinUrl" => u.LinkedinUrl, "facebookUrl" => u.FacebookUrl,
        "instagramUrl" => u.InstagramUrl, "googleMapsUrl" => u.GoogleMapsUrl, "additionalInfo" => u.AdditionalInfo,
        _ => null,
    };

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ Excel import

    public async Task<ImportResultDto> ImportAsync(User caller, Guid companyId, IFormFile? file)
    {
        CurrentUser.EnsureCompanyAccess(caller, companyId);
        if (file is null || file.Length == 0) throw ApiException.BadRequest("FILE_REQUIRED", "An Excel file is required");
        if (file.Length > 10 * 1024 * 1024) throw ApiException.BadRequest("FILE_TOO_LARGE", "Excel file must be smaller than 10 MB");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".xlsx" or ".xls")) throw ApiException.BadRequest("INVALID_FILE", "Only .xlsx and .xls files are supported");

        DataTable table;
        try
        {
            await using var stream = file.OpenReadStream();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            using var reader = ExcelReaderFactory.CreateReader(ms);
            var ds = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true },
            });
            table = ds.Tables.Count > 0 ? ds.Tables[0] : throw ApiException.BadRequest("INVALID_FILE", "The workbook has no sheets");
        }
        catch (ApiException) { throw; }
        catch (Exception)
        {
            throw ApiException.BadRequest("INVALID_FILE", "The Excel file could not be read");
        }

        var map = MapColumns(table);
        if (!map.ContainsKey("firstName") || !map.ContainsKey("lastName") || !map.ContainsKey("email"))
            throw ApiException.BadRequest("INVALID_TEMPLATE", "Required columns are missing: Ad, Soyad, E-poçt");
        if (table.Rows.Count > 2000) throw ApiException.BadRequest("VALIDATION_ERROR", "At most 2000 rows can be imported at once");

        var result = new ImportResultDto();
        await using var tx = await db.Database.BeginTransactionAsync();
        var company = await users.LockCompanyAsync(companyId);
        var used = await users.LimitedUsers(companyId).CountAsync();
        var existingEmails = (await db.Users.Where(u => u.CompanyId == companyId).Select(u => u.Email).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            var row = table.Rows[i];
            string? Get(string key) => map.TryGetValue(key, out var col) ? CellText(row[col]) : null;

            if (Columns.All(c => string.IsNullOrWhiteSpace(Get(c.Key)))) continue; // blank row

            try
            {
                if (used >= company.UserLimit)
                    throw new ApiException(400, "USER_LIMIT_EXCEEDED", "user limit reached");
                var email = Validation.NormalizeEmail(Get("email"));
                if (existingEmails.Contains(email)) throw ApiException.Conflict("EMAIL_TAKEN", "email already in use");

                var password = Get("password");
                string? generated = null;
                if (string.IsNullOrWhiteSpace(password)) password = generated = PasswordService.Generate();
                Validation.ValidatePassword(password);

                var user = new User
                {
                    CompanyId = companyId,
                    Email = email,
                    Role = Role.Employee,
                    IsActive = true,
                    CanEdit = false,
                    MustChangePassword = true,
                    FirstName = Validation.RequireText(Get("firstName"), "firstName", 100),
                    LastName = Validation.RequireText(Get("lastName"), "lastName", 100),
                    MiddleName = Validation.Patch(Get("middleName"), null),
                    JobTitle = Validation.Patch(Get("jobTitle"), null),
                    Phone1 = Validation.Patch(Get("phone1"), null),
                    Phone2 = Validation.Patch(Get("phone2"), null),
                    WhatsappPhone = Validation.Patch(Get("whatsappPhone"), null),
                    ExtensionNumber = Validation.Patch(Get("extensionNumber"), null),
                    Address = Validation.Patch(Get("address"), null),
                    Birthday = Validation.Patch(Get("birthday"), null),
                    LinkedinUrl = Validation.Patch(Get("linkedinUrl"), null),
                    FacebookUrl = Validation.Patch(Get("facebookUrl"), null),
                    InstagramUrl = Validation.Patch(Get("instagramUrl"), null),
                    GoogleMapsUrl = Validation.Patch(Get("googleMapsUrl"), null),
                    AdditionalInfo = Validation.Patch(Get("additionalInfo"), null),
                };
                user.PasswordHash = passwords.Hash(user, password);

                await tx.CreateSavepointAsync("row");
                db.Users.Add(user);
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (DbUpdateException ex)
                {
                    await tx.RollbackToSavepointAsync("row");
                    db.Entry(user).State = EntityState.Detached;
                    foreach (var e in db.ChangeTracker.Entries<AuditLog>().Where(e => e.State == EntityState.Added).ToList())
                        e.State = EntityState.Detached;
                    if (DbErrors.IsUniqueViolation(ex, AppDbContext.EmailIndexName))
                        throw ApiException.Conflict("EMAIL_TAKEN", "email already in use");
                    throw;
                }

                existingEmails.Add(email);
                used++;
                result.Created++;
                if (generated is not null) result.GeneratedCredentials.Add(new GeneratedCredentialDto(rowNumber, email, generated));
            }
            catch (ApiException ex)
            {
                result.Failed++;
                result.Errors.Add($"Sətir {rowNumber}: {ex.Message}");
            }
        }

        Audit(caller, "IMPORT_EXCEL", "Company", companyId.ToString(), new { created = result.Created, failed = result.Failed });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return result;
    }

    private static Dictionary<string, int> MapColumns(DataTable table)
    {
        static string Norm(string s) => new string(s.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var lookup = new Dictionary<string, string>();
        foreach (var (key, header) in Columns)
        {
            lookup[Norm(key)] = key;
            lookup[Norm(header)] = key;
        }
        lookup["email"] = "email"; lookup["epoct"] = "email"; lookup["telefon"] = "phone1";
        lookup["vezife"] = "jobTitle"; lookup["position"] = "jobTitle"; lookup["sifre"] = "password";

        var map = new Dictionary<string, int>();
        for (var c = 0; c < table.Columns.Count; c++)
        {
            var name = Norm(table.Columns[c].ColumnName);
            if (lookup.TryGetValue(name, out var key) && !map.ContainsKey(key)) map[key] = c;
        }
        return map;
    }

    private static string? CellText(object? value) => value switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd"),
        double n when Math.Abs(n % 1) < double.Epsilon => ((long)n).ToString(),
        _ => value.ToString()?.Trim(),
    };

    // ------------------------------------------------------------------ HTML / ZIP

    public async Task<FileResultData> HtmlUserAsync(User caller, Guid userId)
    {
        var user = await users.GetDetailedAsync(userId);
        UserService.EnsureCanView(caller, user);
        var html = await RenderHtmlAsync(user);
        return new FileResultData(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", $"{SafeName(user.FullName)}.html");
    }

    public async Task<FileResultData> HtmlCompanyAsync(User caller, Guid companyId)
    {
        var list = await LoadCompanyUsersAsync(caller, companyId);
        Audit(caller, "EXPORT_HTML", "Company", companyId.ToString(), new { count = list.Count });
        await db.SaveChangesAsync();
        return new FileResultData(await ZipAsync(list), "application/zip", "business-cards.zip");
    }

    public async Task<FileResultData> HtmlSelectedAsync(User caller, List<Guid> ids)
    {
        var list = await LoadUsersAsync(caller, ids);
        Audit(caller, "EXPORT_HTML", "User", null, new { count = list.Count });
        await db.SaveChangesAsync();
        return new FileResultData(await ZipAsync(list), "application/zip", "business-cards-selected.zip");
    }

    private async Task<byte[]> ZipAsync(List<User> list)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = new StringBuilder("<!doctype html><html lang=\"az\"><head><meta charset=\"utf-8\"><title>Vizitkartlar</title></head><body><h1>Vizitkartlar</h1><ul>");
            foreach (var u in list)
            {
                var name = SafeName(u.FullName);
                var file = name + ".html";
                for (var n = 2; !used.Add(file); n++) file = $"{name}-{n}.html";
                var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
                await using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    await w.WriteAsync(await RenderHtmlAsync(u));
                index.Append($"<li><a href=\"{H(Uri.EscapeDataString(file))}\">{H(u.FullName)}</a></li>");
            }
            index.Append("</ul></body></html>");
            var idx = zip.CreateEntry("index.html");
            await using var iw = new StreamWriter(idx.Open(), new UTF8Encoding(false));
            await iw.WriteAsync(index.ToString());
        }
        return ms.ToArray();
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>Self-contained offline copy of the public card page; images embedded as data URIs, no scripts.</summary>
    private async Task<string> RenderHtmlAsync(User u)
    {
        // Same rows, labels and visibility as the public card (hidden links and metadata are excluded).
        var card = VisibleCard.Build(u);
        string? vcfHref = null;
        if (card.ContactCard.Active)
            vcfHref = "data:text/vcard;charset=utf-8;base64," +
                      Convert.ToBase64String(Encoding.UTF8.GetBytes(card.BuildVcf(await VcfPhotoAsync(u.PhotoUrl))));
        var images = new CardHtmlImages(
            await ImageAsDataUriAsync(u.PhotoUrl),
            await ImageAsDataUriAsync(u.CardBackgroundUrl),
            await ImageAsDataUriAsync(u.Company?.LogoUrl),
            vcfHref);
        return CardHtmlRenderer.Render(card, images);
    }

    // ------------------------------------------------------------------ image helpers

    private async Task<string?> ImageAsDataUriAsync(string? url)
    {
        var data = await ImageAsDataAsync(url);
        return data is { } d ? $"data:{d.Mime};base64,{Convert.ToBase64String(d.Bytes)}" : null;
    }

    /// <summary>Resolves data URLs and local /uploads files. Remote URLs are not fetched (no SSRF).</summary>
    private async Task<(byte[] Bytes, string Mime)?> ImageAsDataAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            var meta = comma > 5 ? url[5..comma] : "";
            if (comma < 0 || !meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            try { return (Convert.FromBase64String(url[(comma + 1)..]), meta[..meta.IndexOf(';')]); }
            catch (FormatException) { return null; }
        }
        if (url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetFullPath(Path.IsPathRooted(storageOptions.Value.UploadsPath)
                ? storageOptions.Value.UploadsPath
                : Path.Combine(env.ContentRootPath, storageOptions.Value.UploadsPath));
            var relative = Uri.UnescapeDataString(url["/uploads/".Length..].Split('?')[0]);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            var mime = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg",
            };
            return (await File.ReadAllBytesAsync(full), mime);
        }
        return null;
    }

    private static string SafeName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : c == ' ' ? '-' : '\0')
            .Where(c => c != '\0').ToArray()).Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "card" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
