using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Boneiset.Server;
using Boneiset.Shared;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Cryptography.X509Certificates;

var createAdmin = args.Length == 2 && args[0] == "--create-admin";
var resetPassword = args.Length == 2 && args[0] == "--reset-password";
var builder = WebApplication.CreateBuilder(createAdmin || resetPassword ? [] : args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Database. No database credentials are supplied by default.");
var adminKey = builder.Configuration["AdminApiKey"] ?? "";
if (adminKey.Length != 64 || !adminKey.All(Uri.IsHexDigit))
    throw new InvalidOperationException("Set AdminApiKey to 32 random bytes encoded as 64 hexadecimal characters.");
var adminKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(adminKey));
var retentionDays = builder.Configuration.GetValue("LocationRetentionDays", 30);
var offlineMinutes = builder.Configuration.GetValue("OfflineAfterMinutes", 60);
if (retentionDays is < 1 or > 30 || offlineMinutes is < 15 or > 10080)
    throw new InvalidOperationException("LocationRetentionDays must be 1..30; OfflineAfterMinutes must be 15..10080.");
if (!Uri.TryCreate(builder.Configuration["PublicUrl"], UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != "https")
    throw new InvalidOperationException("PublicUrl must use HTTPS.");

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<Store>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
    foreach (var page in new[] { "/Enrollment", "/Users", "/Audit", "/Policy" })
        options.Conventions.AuthorizePage(page, "Admin");
}).AddMvcOptions(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "Boneiset.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/Error?code=403";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = DashboardSecurity.ValidateCookie;
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.Configure<FormOptions>(options => { options.ValueLengthLimit = 4096; options.ValueCountLimit = 64; });
var protection = builder.Services.AddDataProtection().SetApplicationName("Boneiset");
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(keyPath))
    throw new InvalidOperationException("Set DataProtection__KeyPath to a private persistent directory.");
if (!string.IsNullOrWhiteSpace(keyPath)) protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certificatePath))
    protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certificatePath, builder.Configuration["DataProtection:CertificatePassword"]));
else if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    if (IPAddress.TryParse(builder.Configuration["TrustedProxy"], out var proxy)) options.KnownProxies.Add(proxy);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetFixedWindowLimiter("login:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("other"));
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("enroll", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("dashboard", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.Headers.CacheControl = "no-cache, no-store";
    if (context.Request.Path.StartsWithSegments("/api"))
        await context.Response.WriteAsJsonAsync(new { error = "Server could not complete the request." });
    else
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync("""
            <!doctype html><html lang="id"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Layanan tidak tersedia | Boneiset</title><link rel="stylesheet" href="/site.css"></head><body><main>
            <section class="empty"><h1>Layanan belum dapat diakses.</h1><p>Permintaan tidak selesai. Coba lagi beberapa saat.
            Periksa data sebelum mengulangi perubahan.</p><a class="button" href="/">Kembali ke register</a></section></main></body></html>
            """);
    }
}));
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps && context.Request.Path != "/health")
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = "HTTPS required." });
        return;
    }
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), pipeline => pipeline.UseStatusCodePagesWithReExecute("/Error", "?code={0}"));
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages().RequireRateLimiting("dashboard");
app.MapGet("/health", async (Store store) =>
{
    try { return await store.IsReadyAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503); }
    catch (NpgsqlException) { return Results.StatusCode(503); }
});

app.MapPost("/api/v1/enroll", async (EnrollmentRequest request, Store store) =>
{
    if (request.Token is null || request.Token.Length != 64 || !request.Token.All(Uri.IsHexDigit) ||
        string.IsNullOrWhiteSpace(request.MachineName) || request.MachineName.Length > 100)
        return Results.BadRequest(new { error = "Invalid enrollment data." });
    var enrollment = await store.EnrollAsync(request);
    return enrollment is null ? Results.Unauthorized() : Results.Ok(enrollment);
}).RequireRateLimiting("enroll");

app.MapPost("/api/v1/devices/{id:guid}/reports", async (Guid id, DeviceReport report, HttpContext context, Store store) =>
{
    var key = Bearer(context);
    if (key.Length != 64 || !key.All(Uri.IsHexDigit)) return Results.Unauthorized();
    var error = Protocol.Validate(report, DateTimeOffset.UtcNow);
    if (error is not null) return Results.BadRequest(new { error });
    var accepted = await store.AcceptAsync(id, key, report, retentionDays);
    return accepted is null ? Results.Unauthorized() : Results.Ok(new { accepted = true, duplicate = !accepted.Value });
}).RequireRateLimiting("api");

var admin = app.MapGroup("/api/v1/admin").RequireRateLimiting("api");
admin.AddEndpointFilter(async (context, next) =>
{
    var key = Bearer(context.HttpContext);
    if (key.Length != 64 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), adminKeyHash))
        return Results.Unauthorized();
    return await next(context);
});
admin.MapPost("/enrollment-tokens", async (Store store) => Results.Ok(new { token = await store.IssueTokenAsync("bootstrap-admin"), expiresInMinutes = 60 }));
admin.MapGet("/devices", async (string? query, string? branch, int? offset, Store store) =>
    Results.Ok(await store.DevicesAsync(TrimQuery(query), TrimQuery(branch), PageOffset(offset), offlineMinutes, retentionDays)));
admin.MapGet("/export/assets.csv", async (string? query, string? branch, Store store) =>
{
    var rows = await store.ExportDevicesAsync(TrimQuery(query), TrimQuery(branch));
    // ponytail: bound in-memory exports to 10,000 assets; use paginated JSON or streaming exports above that size.
    if (rows.GetArrayLength() > 10000) return Results.BadRequest(new { error = "Export exceeds 10,000 assets; filter by branch or use paginated JSON." });
    return Results.File(Display.AssetCsv(rows), "text/csv; charset=utf-8", "boneiset-assets.csv");
});
admin.MapGet("/devices/{id:guid}", async (Guid id, Store store) =>
{
    var devices = await store.DeviceAsync(id);
    return devices.GetArrayLength() == 0 ? Results.NotFound() : Results.Ok(devices[0]);
});
admin.MapGet("/devices/{id:guid}/locations", async (Guid id, int? offset, Store store) =>
    Results.Ok(await store.LocationsAsync(id, PageOffset(offset), retentionDays)));
admin.MapGet("/applications", async (string? query, Guid? deviceId, int? offset, Store store) =>
    Results.Ok(await store.ApplicationsAsync(TrimQuery(query), deviceId, PageOffset(offset))));
admin.MapGet("/devices/{id:guid}/software-events", async (Guid id, int? offset, Store store) =>
    Results.Ok(await store.SoftwareEventsAsync(id, PageOffset(offset))));
admin.MapPut("/devices/{id:guid}/asset", async (Guid id, AssetUpdate update, Store store) =>
{
    if (!update.IsValid()) return Results.BadRequest(new { error = "Invalid asset metadata." });
    try { return await store.UpdateAssetAsync(id, update, "bootstrap-admin") ? Results.NoContent() : Results.NotFound(); }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    { return Results.Conflict(new { error = "Asset tag already used." }); }
});
admin.MapPost("/devices/{id:guid}/revoke", async (Guid id, Store store) =>
    await store.RevokeAsync(id, "bootstrap-admin") ? Results.NoContent() : Results.NotFound());
admin.MapGet("/audit", async (int? offset, Store store) => Results.Ok(await store.AuditAsync(PageOffset(offset))));
admin.MapGet("/software-policy", async (Store store) => Results.Ok(await store.PoliciesAsync()));
admin.MapPut("/software-policy", async (PolicyUpdate update, Store store) =>
{
    if (string.IsNullOrWhiteSpace(update.Name) || update.Name.Length > 500) return Results.BadRequest();
    await store.SetPolicyAsync(update.Name.Trim().ToLowerInvariant(), update.Remove, "bootstrap-admin");
    return Results.NoContent();
});

await app.Services.GetRequiredService<Store>().InitializeAsync();
if (createAdmin || resetPassword)
{
    var password = Environment.GetEnvironmentVariable("BONEISET_ADMIN_PASSWORD") ?? "";
    var store = app.Services.GetRequiredService<Store>();
    var error = createAdmin ? await store.CreateUserAsync(args[1], password, "Admin", "local-bootstrap", firstOnly: true)
        : await store.ResetPasswordLocallyAsync(args[1], password) ? null : "Akun tidak ditemukan atau kata sandi tidak valid (14-128 karakter).";
    if (error is not null) throw new InvalidOperationException(error);
    Console.WriteLine(createAdmin ? "Akun admin awal dibuat." : "Kata sandi direset; semua sesi lama tidak berlaku.");
    return;
}
await app.RunAsync();

static string Bearer(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
}
static int PageOffset(int? offset) => Math.Clamp(offset ?? 0, 0, 1000000);
static string TrimQuery(string? query) => (query ?? "")[..Math.Min(query?.Length ?? 0, 200)];
public sealed record PolicyUpdate(string Name, bool Remove = false);
