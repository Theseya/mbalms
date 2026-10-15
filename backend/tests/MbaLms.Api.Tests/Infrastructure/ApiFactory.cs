using MbaLms.Api.Data;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace MbaLms.Api.Tests.Infrastructure;

/// <summary>Runs the real API against a throwaway PostgreSQL container (requires Docker).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ManagerEmail = "manager@test.local";
    public const string ManagerPassword = "Manager-Test-1";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ControllableTimeProvider Clock { get; } = new();

    /// <summary>
    /// Test-only SaveChanges failure hook. Registered solely in ConfigureTestServices —
    /// never referenced from the production API project.
    /// </summary>
    public LessonChangeLogSaveFailInterceptor SaveFail { get; } = new();

    /// <summary>Connection string of the migrated test database.</summary>
    public string ConnectionString => _db.GetConnectionString();

    /// <summary>Document storage of this test run; shared by restarted hosts, removed after the run.</summary>
    public string DocumentsPath { get; } = Path.Combine(Path.GetTempPath(), "mbalms-tests-docs-" + Guid.NewGuid().ToString("N"));

    /// <summary>Small limit so oversize uploads are cheap to send; production default is 20 MB.</summary>
    public const long MaxProgramPdfBytes = 256 * 1024;

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(DocumentsPath)) Directory.Delete(DocumentsPath, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Seed:ManagerEmail", ManagerEmail);
        builder.UseSetting("Seed:ManagerPassword", ManagerPassword);
        builder.UseSetting("RateLimiting:LoginPermitsPerMinute", "10000");
        builder.UseSetting("RateLimiting:PasswordMailPermitsPerWindow", "10000");
        builder.UseSetting("Email:Provider", "Development");
        builder.UseSetting("Email:PublicBaseUrl", "http://127.0.0.1:5173");
        builder.UseSetting("Email:TokenLifespan", "01:00:00");
        builder.UseSetting("Auth:SessionValidationInterval", "00:00:00");
        builder.UseSetting("Documents:StoragePath", DocumentsPath);
        builder.UseSetting("Documents:MaxProgramPdfBytes", MaxProgramPdfBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureLogging(logging =>
            logging.AddProvider(new FileErrorLoggerProvider(Path.Combine(AppContext.BaseDirectory, "test-server-errors.log"))));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.RemoveAll<AppTime>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<AppTime>();

            services.AddSingleton(SaveFail);
            // Re-bind AppDbContext so SaveChanges goes through the test interceptor (armed only in atomicity tests).
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>((_, o) =>
            {
                o.UseNpgsql(_db.GetConnectionString());
                o.AddInterceptors(SaveFail);
            });
        });
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
