using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
builder.Services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AppTime>();
builder.Services.AddSingleton<MbaLms.Api.Infrastructure.Import.ImportSessionStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<NotificationService>();

var connectionString = config.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Connection string 'ConnectionStrings:Default' is not configured.");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager();

// Keys protect auth/antiforgery cookies; they must survive restarts or every user gets signed out.
var keysPath = config["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection().SetApplicationName("MbaLms").PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// "Always" in production behind HTTPS; "SameAsRequest" lets plain-HTTP local runs work.
var cookieSecurePolicy = config.GetValue("Auth:CookieSecurePolicy", CookieSecurePolicy.SameAsRequest);

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
// Sessions are re-checked against the security stamp, so a password reset or account deletion ends existing sessions.
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
    o.ValidationInterval = config.GetValue("Auth:SessionValidationInterval", TimeSpan.FromMinutes(1)));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "mbalms.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSecurePolicy;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    // API: answer with status codes instead of redirecting to a login page.
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
// Deny by default: an endpoint is public only with an explicit [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Roles.Manager, p => p.RequireRole(Roles.Manager))
    .AddPolicy(Roles.Student, p => p.RequireRole(Roles.Student));

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "mbalms.af";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSecurePolicy;
});

// Only behind a trusted reverse proxy that is the sole way to reach the API (as in docker-compose).
var useForwardedHeaders = config.GetValue("ForwardedHeaders:Enabled", false);
if (useForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var loginPermitLimit = config.GetValue("RateLimiting:LoginPermitsPerMinute", 20);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPermitLimit, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services
    .AddControllers(o =>
    {
        o.Filters.Add<ValidateAntiforgeryFilter>();
    })
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(o =>
    {
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var pd = new ValidationProblemDetails(ValidationErrors.FromModelState(ctx.ModelState))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = ErrorCodes.ValidationFailed
            };
            pd.Extensions["code"] = ErrorCodes.ValidationFailed;
            return new BadRequestObjectResult(pd);
        };
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

if (useForwardedHeaders) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "MBA Mini-LMS API"));
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

if (config.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

using (var scope = app.Services.CreateScope())
{
    await DbSeeder.SeedAsync(scope.ServiceProvider);
}

app.Run();

public partial class Program;
