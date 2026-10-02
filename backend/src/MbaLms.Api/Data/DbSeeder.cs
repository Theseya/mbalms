using MbaLms.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Data;

public class SeedOptions
{
    public const string Section = "Seed";

    public string ProgramName { get; set; } = "MBA";

    /// <summary>Initial manager account. Created only if both values are provided (env vars / user secrets).</summary>
    public string? ManagerEmail { get; set; }
    public string? ManagerPassword { get; set; }
}

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SeedOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbSeeder));

        foreach (var role in new[] { Roles.Manager, Roles.Student })
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));
        }

        if (!await db.Programs.AnyAsync(ct))
        {
            db.Programs.Add(new MbaProgram { Id = Guid.CreateVersion7(), Name = options.ProgramName });
            await db.SaveChangesAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(options.ManagerEmail) || string.IsNullOrWhiteSpace(options.ManagerPassword))
        {
            if (!(await users.GetUsersInRoleAsync(Roles.Manager)).Any())
                logger.LogWarning("No manager account exists. Set Seed__ManagerEmail and Seed__ManagerPassword to create one.");
            return;
        }

        if (await users.FindByEmailAsync(options.ManagerEmail) is not null) return;

        var manager = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = options.ManagerEmail,
            Email = options.ManagerEmail,
            EmailConfirmed = true
        };
        var result = await users.CreateAsync(manager, options.ManagerPassword);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create the manager account: {Errors}",
                string.Join(", ", result.Errors.Select(e => e.Code)));
            return;
        }
        await users.AddToRoleAsync(manager, Roles.Manager);
        logger.LogInformation("Manager account created for {Email}", options.ManagerEmail);
    }
}
