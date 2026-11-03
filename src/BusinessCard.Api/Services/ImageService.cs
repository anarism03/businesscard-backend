using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace BusinessCard.Api.Services;

public class StorageOptions
{
    /// <summary>Public folder served at <c>/uploads</c>.</summary>
    public string UploadsPath { get; set; } = "wwwroot/uploads";
    /// <summary>Private folder for untouched originals (never served statically).</summary>
    public string OriginalsPath { get; set; } = "storage/originals";
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxSourcePixels { get; set; } = 60_000_000;
}

/// <summary>
/// Crop rectangle in pixels of the ORIGINAL image after EXIF orientation is applied; origin is the top-left corner.
/// Either all six values are sent or none (no crop = whole image).
/// </summary>
public class CropRequest
{
    /// <example>120</example>
    [FromForm(Name = "x")]
    public double? X { get; set; }
    /// <example>80</example>
    [FromForm(Name = "y")]
    public double? Y { get; set; }
    /// <example>800</example>
    [FromForm(Name = "width")]
    public double? Width { get; set; }
    /// <example>800</example>
    [FromForm(Name = "height")]
    public double? Height { get; set; }
    /// <summary>Width of the oriented original the client measured coordinates against.</summary>
    /// <example>1080</example>
    [FromForm(Name = "sourceWidth")]
    public double? SourceWidth { get; set; }
    /// <summary>Height of the oriented original the client measured coordinates against.</summary>
    /// <example>1440</example>
    [FromForm(Name = "sourceHeight")]
    public double? SourceHeight { get; set; }

    public bool IsEmpty() => X is null && Y is null && Width is null && Height is null && SourceWidth is null && SourceHeight is null;
    public bool IsComplete() => X is not null && Y is not null && Width is not null && Height is not null && SourceWidth is not null && SourceHeight is not null;
}

public class CropInfoDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
}

public class ImageResultDto
{
    /// <summary>ID of the stored original; use it for re-cropping.</summary>
    public Guid ImageId { get; set; }
    public Guid OriginalImageId { get; set; }
    /// <example>profilePhoto</example>
    public string Kind { get; set; } = "";
    /// <summary>Public URL of the cropped result (also written to the user/company record).</summary>
    /// <example>/uploads/profile-photo/2c1d....jpg</example>
    public string Url { get; set; } = "";
    /// <summary>Authenticated URL of the untouched original.</summary>
    /// <example>/api/images/5a7b.../original</example>
    public string OriginalUrl { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public CropInfoDto Crop { get; set; } = new();
    public Guid? UserId { get; set; }
    public Guid CompanyId { get; set; }
}

public class ImageService(AppDbContext db, IOptions<StorageOptions> options, IWebHostEnvironment env)
{
    private readonly StorageOptions _o = options.Value;

    private static readonly Dictionary<ImageKind, (string Folder, int MaxEdge, SKEncodedImageFormat Format, string Ext, string Mime)> Profiles = new()
    {
        [ImageKind.ProfilePhoto] = ("profile-photo", 1024, SKEncodedImageFormat.Jpeg, ".jpg", "image/jpeg"),
        [ImageKind.CardBackground] = ("card-background", 2048, SKEncodedImageFormat.Jpeg, ".jpg", "image/jpeg"),
        [ImageKind.CompanyLogo] = ("company-logo", 1024, SKEncodedImageFormat.Png, ".png", "image/png"),
    };

    public static string KindName(ImageKind kind) => kind switch
    {
        ImageKind.ProfilePhoto => "profilePhoto",
        ImageKind.CardBackground => "cardBackground",
        _ => "companyLogo",
    };

    private string Root(string path) => Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);

    // ------------------------------------------------------------------ authorization

    public static void EnsureCanEditUserImage(User caller, User target)
    {
        if (caller.Role == Role.SuperAdmin) return;
        if (caller.Id == target.Id)
        {
            if (!UserService.CanEditOwnProfile(caller))
                throw ApiException.Forbidden("Profile editing is disabled for your account");
            return;
        }
        if (caller.Role == Role.CompanyAdmin && caller.CompanyId == target.CompanyId && target.Role != Role.SuperAdmin) return;
        throw ApiException.Forbidden();
    }

    private async Task EnsureCanEditAsync(User caller, StoredImage image)
    {
        if (image.Kind == ImageKind.CompanyLogo)
        {
            CurrentUser.EnsureCompanyAccess(caller, image.CompanyId);
            return;
        }
        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == image.UserId) ?? throw ApiException.NotFound("Image owner not found");
        EnsureCanEditUserImage(caller, target);
    }

    // ------------------------------------------------------------------ upload

    public async Task<ImageResultDto> UploadForUserAsync(User caller, User target, ImageKind kind, IFormFile? file, CropRequest crop)
    {
        EnsureCanEditUserImage(caller, target);
        var image = await StoreAsync(caller, kind, target.CompanyId!.Value, target.Id, file, crop);
        if (kind == ImageKind.ProfilePhoto) target.PhotoUrl = image.ResultUrl;
        else target.CardBackgroundUrl = image.ResultUrl;
        await CommitReplacingAsync(image);
        return ToDto(image);
    }

    public async Task<ImageResultDto> UploadCompanyLogoAsync(User caller, Company company, IFormFile? file, CropRequest crop)
    {
        CurrentUser.EnsureCompanyAccess(caller, company.Id);
        var image = await StoreAsync(caller, ImageKind.CompanyLogo, company.Id, null, file, crop);
        company.LogoUrl = image.ResultUrl;
        await CommitReplacingAsync(image);
        return ToDto(image);
    }

    public async Task<ImageResultDto> RecropAsync(User caller, Guid imageId, CropRequest crop)
    {
        var image = await db.StoredImages.FirstOrDefaultAsync(i => i.Id == imageId) ?? throw ApiException.NotFound("Image not found");
        await EnsureCanEditAsync(caller, image);
        if (!crop.IsComplete()) throw ApiException.BadRequest("INVALID_CROP", "x, y, width, height, sourceWidth and sourceHeight are required");

        var originalFile = Path.Combine(Root(_o.OriginalsPath), image.OriginalPath);
        if (!File.Exists(originalFile)) throw ApiException.NotFound("Original image file is missing");
        var bytes = await File.ReadAllBytesAsync(originalFile);

        var oldResult = image.ResultPath;
        RenderResult(image, bytes, crop);

        // Point the owning record at the new result.
        if (image.Kind == ImageKind.CompanyLogo)
        {
            var company = await db.Companies.FirstAsync(c => c.Id == image.CompanyId);
            company.LogoUrl = image.ResultUrl;
        }
        else
        {
            var user = await db.Users.FirstAsync(u => u.Id == image.UserId);
            if (image.Kind == ImageKind.ProfilePhoto) user.PhotoUrl = image.ResultUrl;
            else user.CardBackgroundUrl = image.ResultUrl;
        }
        image.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        TryDelete(Path.Combine(Root(_o.UploadsPath), oldResult));
        return ToDto(image);
    }

    public async Task<(string Path, string ContentType)> GetOriginalAsync(User caller, Guid imageId)
    {
        var image = await db.StoredImages.FirstOrDefaultAsync(i => i.Id == imageId) ?? throw ApiException.NotFound("Image not found");
        await EnsureCanEditAsync(caller, image);
        var file = Path.Combine(Root(_o.OriginalsPath), image.OriginalPath);
        if (!File.Exists(file)) throw ApiException.NotFound("Original image file is missing");
        return (file, image.OriginalContentType);
    }

    public async Task<ImageResultDto> GetAsync(User caller, Guid imageId)
    {
        var image = await db.StoredImages.FirstOrDefaultAsync(i => i.Id == imageId) ?? throw ApiException.NotFound("Image not found");
        await EnsureCanEditAsync(caller, image);
        return ToDto(image);
    }

    private async Task<StoredImage> StoreAsync(User caller, ImageKind kind, Guid companyId, Guid? userId, IFormFile? file, CropRequest crop)
    {
        if (file is null || file.Length == 0) throw ApiException.BadRequest("FILE_REQUIRED", "An image file is required");
        if (file.Length > _o.MaxUploadBytes)
            throw ApiException.BadRequest("FILE_TOO_LARGE", $"Image must be smaller than {_o.MaxUploadBytes / 1024 / 1024} MB");
        if (!crop.IsEmpty() && !crop.IsComplete())
            throw ApiException.BadRequest("INVALID_CROP", "Send all of x, y, width, height, sourceWidth, sourceHeight or none of them");

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        var image = new StoredImage
        {
            Kind = kind,
            CompanyId = companyId,
            UserId = userId,
            CreatedById = caller.Id,
        };

        // Decoding validates the content (extension / declared content type are not trusted).
        var (ext, mime) = DetectFormat(bytes);
        RenderResult(image, bytes, crop);

        var originalsDir = Root(_o.OriginalsPath);
        Directory.CreateDirectory(originalsDir);
        image.OriginalPath = $"{image.Id:N}{ext}";
        image.OriginalContentType = mime;
        await File.WriteAllBytesAsync(Path.Combine(originalsDir, image.OriginalPath), bytes);

        db.StoredImages.Add(image);
        return image;
    }

    /// <summary>Saves the new image and removes the previous image (record + files) of the same owner and kind.</summary>
    private async Task CommitReplacingAsync(StoredImage image)
    {
        var previous = await db.StoredImages
            .Where(i => i.Id != image.Id && i.CompanyId == image.CompanyId && i.UserId == image.UserId && i.Kind == image.Kind)
            .ToListAsync();
        db.StoredImages.RemoveRange(previous);
        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            TryDelete(Path.Combine(Root(_o.UploadsPath), image.ResultPath));
            TryDelete(Path.Combine(Root(_o.OriginalsPath), image.OriginalPath));
            throw;
        }
        foreach (var old in previous)
        {
            TryDelete(Path.Combine(Root(_o.UploadsPath), old.ResultPath));
            TryDelete(Path.Combine(Root(_o.OriginalsPath), old.OriginalPath));
        }
    }

    // ------------------------------------------------------------------ pixels

    private void RenderResult(StoredImage image, byte[] bytes, CropRequest crop)
    {
        using var oriented = DecodeOriented(bytes, _o.MaxSourcePixels);
        image.OriginalWidth = oriented.Width;
        image.OriginalHeight = oriented.Height;

        var rect = ResolveCrop(crop, oriented.Width, oriented.Height);
        image.CropX = rect.Left;
        image.CropY = rect.Top;
        image.CropWidth = rect.Width;
        image.CropHeight = rect.Height;

        var profile = Profiles[image.Kind];
        var scale = Math.Min(1.0, (double)profile.MaxEdge / Math.Max(rect.Width, rect.Height));
        var outW = Math.Max(1, (int)Math.Round(rect.Width * scale));
        var outH = Math.Max(1, (int)Math.Round(rect.Height * scale));

        var info = new SKImageInfo(outW, outH, SKColorType.Rgba8888,
            profile.Format == SKEncodedImageFormat.Png ? SKAlphaType.Premul : SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Cannot allocate image surface");
        var canvas = surface.Canvas;
        canvas.Clear(profile.Format == SKEncodedImageFormat.Png ? SKColors.Transparent : SKColors.White);
        using (var src = SKImage.FromBitmap(oriented))
        using (var paint = new SKPaint())
        {
            canvas.DrawImage(src,
                new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                new SKRect(0, 0, outW, outH),
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
        }
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        // Re-encoding drops all metadata (EXIF GPS etc.) from the public file.
        using var data = snapshot.Encode(profile.Format, 88)
                         ?? throw ApiException.BadRequest("INVALID_IMAGE", "The image could not be encoded");

        var folder = Path.Combine(Root(_o.UploadsPath), profile.Folder);
        Directory.CreateDirectory(folder);
        var fileName = $"{Guid.NewGuid():N}{profile.Ext}";
        using (var fs = File.Create(Path.Combine(folder, fileName))) data.SaveTo(fs);

        image.ResultPath = Path.Combine(profile.Folder, fileName);
        image.ResultUrl = $"/uploads/{profile.Folder}/{fileName}";
        image.ResultWidth = outW;
        image.ResultHeight = outH;
    }

    /// <summary>
    /// Validates the crop against the oriented image. If the client measured against a proportionally scaled copy
    /// (same aspect ratio), coordinates are scaled to original pixels; otherwise the request is rejected.
    /// </summary>
    internal static SKRectI ResolveCrop(CropRequest crop, int width, int height)
    {
        if (crop.IsEmpty()) return new SKRectI(0, 0, width, height);

        double sw = crop.SourceWidth!.Value, sh = crop.SourceHeight!.Value;
        if (sw <= 0 || sh <= 0) throw ApiException.BadRequest("INVALID_CROP", "sourceWidth and sourceHeight must be positive");

        var sameSize = Math.Abs(sw - width) <= 1 && Math.Abs(sh - height) <= 1;
        if (!sameSize && Math.Abs(sw / sh - (double)width / height) > 0.01)
            throw ApiException.BadRequest("SOURCE_SIZE_MISMATCH",
                $"sourceWidth/sourceHeight ({sw}x{sh}) do not match the EXIF-oriented image ({width}x{height})");

        double fx = sameSize ? 1 : width / sw, fy = sameSize ? 1 : height / sh;
        double x = crop.X!.Value, y = crop.Y!.Value, w = crop.Width!.Value, h = crop.Height!.Value;
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > sw + 0.5 || y + h > sh + 0.5)
            throw ApiException.BadRequest("INVALID_CROP", "The crop area must be inside the source image");

        var left = (int)Math.Floor(x * fx);
        var top = (int)Math.Floor(y * fy);
        var right = Math.Min(width, (int)Math.Round((x + w) * fx));
        var bottom = Math.Min(height, (int)Math.Round((y + h) * fy));
        if (right - left < 1 || bottom - top < 1) throw ApiException.BadRequest("INVALID_CROP", "The crop area is too small");
        return new SKRectI(left, top, right, bottom);
    }

    /// <summary>Decodes the image and applies its EXIF orientation so pixels match what users see.</summary>
    internal static SKBitmap DecodeOriented(byte[] bytes, int maxPixels)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw ApiException.BadRequest("INVALID_IMAGE", "Unsupported or corrupt image file");
        if ((long)codec.Info.Width * codec.Info.Height > maxPixels)
            throw ApiException.BadRequest("IMAGE_TOO_LARGE", "Image dimensions are too large");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var raw = new SKBitmap(info);
        var result = codec.GetPixels(info, raw.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            raw.Dispose();
            throw ApiException.BadRequest("INVALID_IMAGE", "Unsupported or corrupt image file");
        }
        return ApplyOrigin(raw, codec.EncodedOrigin);
    }

    internal static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft) return src;

        float w = src.Width, h = src.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var dst = new SKBitmap(new SKImageInfo(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType));
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(matrix);
            using var img = SKImage.FromBitmap(src);
            canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default, null);
        }
        src.Dispose();
        return dst;
    }

    private static (string Ext, string Mime) DetectFormat(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw ApiException.BadRequest("INVALID_IMAGE", "Unsupported or corrupt image file");
        return codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => (".jpg", "image/jpeg"),
            SKEncodedImageFormat.Png => (".png", "image/png"),
            SKEncodedImageFormat.Webp => (".webp", "image/webp"),
            SKEncodedImageFormat.Gif => (".gif", "image/gif"),
            SKEncodedImageFormat.Bmp => (".bmp", "image/bmp"),
            _ => throw ApiException.BadRequest("INVALID_IMAGE", "Supported formats: JPEG, PNG, WebP, GIF, BMP"),
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public static ImageResultDto ToDto(StoredImage i) => new()
    {
        ImageId = i.Id,
        OriginalImageId = i.Id,
        Kind = KindName(i.Kind),
        Url = i.ResultUrl,
        OriginalUrl = $"/api/images/{i.Id}/original",
        Width = i.ResultWidth,
        Height = i.ResultHeight,
        OriginalWidth = i.OriginalWidth,
        OriginalHeight = i.OriginalHeight,
        Crop = new CropInfoDto
        {
            X = i.CropX, Y = i.CropY, Width = i.CropWidth, Height = i.CropHeight,
            SourceWidth = i.OriginalWidth, SourceHeight = i.OriginalHeight,
        },
        UserId = i.UserId,
        CompanyId = i.CompanyId,
    };
}
