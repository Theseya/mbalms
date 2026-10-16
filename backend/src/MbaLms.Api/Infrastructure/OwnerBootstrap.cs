using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Infrastructure;

public enum OwnerBootstrapStatus
{
    Created = 0,
    AlreadyExists = 1,
    InvalidInput = 2
}

public sealed record OwnerBootstrapResult(OwnerBootstrapStatus Status, Guid? UserId = null);

/// <summary>
/// One-shot creation of the first Owner. Not an HTTP endpoint and not invoked on ordinary startup.
/// Does not change existing managers, passwords, or sessions.
/// </summary>
public sealed class OwnerBootstrap(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    AppTime time)
{
    public const int ExitCreated = 0;
    public const int ExitInvalid = 1;
    public const int ExitAlreadyExists = 2;

    public async Task<OwnerBootstrapResult> RunAsync(string? email, string? password, CancellationToken ct = default)
    {
        email = email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new OwnerBootstrapResult(OwnerBootstrapStatus.InvalidInput);

        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(881001, 1)", ct);
        try
        {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var name in Roles.All)
        {
            if (!await roles.RoleExistsAsync(name))
                await roles.CreateAsync(new IdentityRole<Guid>(name));
        }

        if ((await users.GetUsersInRoleAsync(Roles.Owner)).Count > 0)
        {
            await tx.RollbackAsync(ct);
            return new OwnerBootstrapResult(OwnerBootstrapStatus.AlreadyExists);
        }

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            return new OwnerBootstrapResult(OwnerBootstrapStatus.InvalidInput);

        var roleResult = await users.AddToRoleAsync(user, Roles.Owner);
        if (!roleResult.Succeeded)
            return new OwnerBootstrapResult(OwnerBootstrapStatus.InvalidInput);

        db.AdminAuditEntries.Add(new AdminAuditEntry
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = time.UtcNow,
            ActorUserId = null,
            ActorEmail = null,
            TargetUserId = user.Id,
            TargetEmail = email,
            Action = AdminAuditAction.OwnerBootstrapped,
            TargetKind = Roles.Owner,
            DetailsJson = """{"source":"bootstrap-owner"}"""
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new OwnerBootstrapResult(OwnerBootstrapStatus.Created, user.Id);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(881001, 1)", ct);
        }
    }
}
