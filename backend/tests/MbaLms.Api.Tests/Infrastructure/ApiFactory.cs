using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace MbaLms.Api.Tests.Infrastructure;

/// <summary>Runs the real API against a throwaway PostgreSQL container (requires Docker).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ManagerEmail = "manager@test.local";
    public const string ManagerPassword = "Manager-Test-1";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Connection string of the migrated test database.</summary>
    public string ConnectionString => _db.GetConnectionString();

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Seed:ManagerEmail", ManagerEmail);
        builder.UseSetting("Seed:ManagerPassword", ManagerPassword);
        builder.UseSetting("RateLimiting:LoginPermitsPerMinute", "10000");
        builder.ConfigureLogging(logging =>
            logging.AddProvider(new FileErrorLoggerProvider(Path.Combine(AppContext.BaseDirectory, "test-server-errors.log"))));
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
