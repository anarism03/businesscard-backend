using System.Text;
using System.Threading.RateLimiting;
using BusinessCard.Api.Common;
using BusinessCard.Api.Data;
using BusinessCard.Api.Infrastructure;
using BusinessCard.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // .xls import (ExcelDataReader)

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------- options
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
builder.Services.Configure<AuthOptions>(config.GetSection("Auth"));
builder.Services.Configure<AnalyticsOptions>(config.GetSection("Analytics"));
builder.Services.Configure<StorageOptions>(config.GetSection("Storage"));
builder.Services.Configure<AppOptions>(config.GetSection("App"));

var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException(
        "Jwt:Key is missing or shorter than 32 characters. Set it with: dotnet user-secrets set \"Jwt:Key\" \"<random 64 chars>\"");

var connectionString = config.GetConnectionString("DefaultConnection");
// Hosts like Render pass the database as a postgresql:// URL, which Npgsql does not parse.
if (string.IsNullOrWhiteSpace(connectionString) &&
    Uri.TryCreate(config["DATABASE_URL"], UriKind.Absolute, out var dbUrl) && dbUrl.Scheme.StartsWith("postgres"))
{
    var userInfo = dbUrl.UserInfo.Split(':', 2);
    connectionString = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = dbUrl.Host,
        Port = dbUrl.IsDefaultPort || dbUrl.Port <= 0 ? 5432 : dbUrl.Port,
        Database = dbUrl.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
    }.ConnectionString;
}
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is missing. Set it with dotnet user-secrets (see README.md).");

// ---------------------------------------------------------------- services
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuditActor>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CompanyService>();
builder.Services.AddScoped<NicknameService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<ExportImportService>();

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        // Return validation errors in the same { code, message } shape as business errors.
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var first = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
                .Select(e => $"{e.Key}: {e.Value!.Errors[0].ErrorMessage}").FirstOrDefault() ?? "Invalid request";
            return new BadRequestObjectResult(new ErrorResponse("VALIDATION_ERROR", first));
        };
    });

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
        o.Events = new JwtBearerEvents
        {
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse("UNAUTHORIZED", "Authentication required"));
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse("FORBIDDEN", "You do not have permission for this operation"));
            },
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, config.GetValue("Auth:LoginAttemptsPerMinute", 10)),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.OnRejected = async (ctx, token) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("TOO_MANY_REQUESTS", "Too many login attempts. Try again in a minute."), token);
});

var corsOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (corsOrigins.Length > 0) p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddBusinessCardSwagger();

var app = builder.Build();

// ---------------------------------------------------------------- startup tasks
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (config.GetValue("Database:MigrateOnStartup", false)) await db.Database.MigrateAsync();
    await SuperAdminSeeder.RunAsync(scope.ServiceProvider, config, app.Logger);
}

// ---------------------------------------------------------------- pipeline
app.UseForwardedHeaders();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

var uploadsPath = config.GetValue<string>("Storage:UploadsPath") ?? "wwwroot/uploads";
var uploadsRoot = Path.IsPathRooted(uploadsPath) ? uploadsPath : Path.Combine(app.Environment.ContentRootPath, uploadsPath);
Directory.CreateDirectory(uploadsRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsRoot),
    RequestPath = "/uploads",
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable",
});

if (app.Environment.IsDevelopment() || config.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "BusinessCard API v1");
        o.DocumentTitle = "BusinessCard API";
    });
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().ExcludeFromDescription();

app.Run();

public partial class Program;
