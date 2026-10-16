using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Infrastructure;

public record StaffDto(Guid Id, string Email, IReadOnlyList<string> Roles, bool Locked);
public record StaffInviteResultDto(bool Queued, Guid? DevelopmentPreviewId);
public record AdminAuditDto(
    Guid Id, DateTimeOffset OccurredAt, string? ActorEmail, string? TargetEmail,
    AdminAuditAction Action, string TargetKind);

public sealed class StaffAccessService(
    AppDbContext db,
    UserManager<AppUser> users,
    CurrentUser current,
    AppTime time,
    IEmailSender emailSender,
    IEmailOutbox outbox,
    PasswordSetupLinkService passwordLinks,
    IHostEnvironment env)
{
    private static readonly DateTimeOffset PermanentLockout = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    public async Task<PagedResult<StaffDto>> ListAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var actor = await RequireActorAsync();
        var actorRoles = await users.GetRolesAsync(actor);
        var accountAdminOnly = actorRoles.Contains(Roles.AccountAdmin) && !actorRoles.Contains(Roles.Owner);

        var staffRoleIds = await db.Roles.AsNoTracking()
            .Where(r => Roles.Staff.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);
        var staffUserIds = await db.UserRoles.AsNoTracking()
            .Where(ur => staffRoleIds.Contains(ur.RoleId))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);

        var q = db.Users.AsNoTracking().Where(u => staffUserIds.Contains(u.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = ManagerControllerBase.ContainsPattern(search.Trim());
            q = q.Where(u => u.Email != null && EF.Functions.ILike(u.Email, pattern));
        }

        var usersPage = await q.OrderBy(u => u.Email).ThenBy(u => u.Id).ToListAsync(ct);
        var items = new List<StaffDto>();
        foreach (var user in usersPage)
        {
            var roles = Roles.Ordered(await users.GetRolesAsync(user));
            var kind = Roles.StaffKind(roles);
            if (kind is null) continue;
            if (accountAdminOnly && kind != Roles.Manager) continue;
            items.Add(new StaffDto(user.Id, user.Email!, roles, IsLocked(user)));
        }

        var total = items.Count;
        var pageItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResult<StaffDto>(pageItems, total, page, pageSize);
    }

    public async Task<StaffInviteResultDto> InviteManagerAsync(string email, CancellationToken ct)
    {
        if (!emailSender.IsConfigured)
            throw AppException.ServiceUnavailable(ErrorCodes.MailNotConfigured);

        var actor = await RequireActorAsync();
        email = email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
            throw AppException.Validation(nameof(email), FieldCodes.Duplicate);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            LockoutEnabled = true
        };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded) throw IdentityFailure(created);
        var role = await users.AddToRoleAsync(user, Roles.Manager);
        if (!role.Succeeded) throw IdentityFailure(role);

        var (_, setupUrl) = await passwordLinks.CreateResetLinkAsync(user);
        var message = PasswordEmailComposer.Invite(email, email, setupUrl);
        await emailSender.SendAsync(message, ct);
        AddAudit(actor, user, email, AdminAuditAction.ManagerInvited, Roles.Manager, """{"invitedRole":"Manager"}""");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        Guid? previewId = null;
        if (env.IsDevelopment() || env.IsEnvironment("Testing"))
            previewId = outbox.LatestFor(email)?.Id;
        return new StaffInviteResultDto(Queued: true, DevelopmentPreviewId: previewId);
    }

    public async Task BlockAsync(Guid id, CancellationToken ct)
    {
        await MutateAsync(id, ct, async (actor, actorRoles, user, kind) =>
        {
            ForbidIfProtectedFromAccountAdmin(actorRoles, kind);
            if (kind == Roles.Owner)
                await EnsureNotLastEffectiveOwnerAsync(user.Id, ct);
            if (IsLocked(user)) return (false, default, kind);

            user.LockoutEnabled = true;
            user.LockoutEnd = PermanentLockout;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            var action = kind switch
            {
                Roles.Owner => AdminAuditAction.OwnerBlocked,
                Roles.AccountAdmin => AdminAuditAction.AccountAdminBlocked,
                _ => AdminAuditAction.ManagerBlocked
            };
            return (true, action, kind);
        });
    }

    public async Task UnblockAsync(Guid id, CancellationToken ct)
    {
        await MutateAsync(id, ct, async (actor, actorRoles, user, kind) =>
        {
            ForbidIfProtectedFromAccountAdmin(actorRoles, kind);
            if (!IsLocked(user)) return (false, default, kind);
            user.LockoutEnd = null;
            var action = kind switch
            {
                Roles.Owner => AdminAuditAction.OwnerUnblocked,
                Roles.AccountAdmin => AdminAuditAction.AccountAdminUnblocked,
                _ => AdminAuditAction.ManagerUnblocked
            };
            return (true, action, kind);
        });
    }

    public async Task RevokeOwnerAsync(Guid id, CancellationToken ct)
    {
        await MutateAsync(id, ct, async (actor, actorRoles, user, kind) =>
        {
            if (!actorRoles.Contains(Roles.Owner))
                throw new AppException(StatusCodes.Status403Forbidden, ErrorCodes.ProtectedAccount);
            if (kind != Roles.Owner)
                throw AppException.NotFound();
            await EnsureNotLastEffectiveOwnerAsync(user.Id, ct);
            var ownerRoleId = await db.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync(ct);
            var link = await db.UserRoles.FirstOrDefaultAsync(ur => ur.UserId == user.Id && ur.RoleId == ownerRoleId, ct);
            if (link is not null) db.UserRoles.Remove(link);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            return (true, AdminAuditAction.OwnerRoleRevoked, Roles.Owner);
        });
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await MutateAsync(id, ct, async (actor, actorRoles, user, kind) =>
        {
            ForbidIfProtectedFromAccountAdmin(actorRoles, kind);
            if (kind == Roles.Owner)
            {
                await EnsureNotLastEffectiveOwnerAsync(user.Id, ct);
                throw new AppException(StatusCodes.Status403Forbidden, ErrorCodes.ProtectedAccount);
            }
            if (kind == Roles.AccountAdmin)
                throw new AppException(StatusCodes.Status403Forbidden, ErrorCodes.ProtectedAccount);

            db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.UserId == user.Id));
            db.Users.Remove(user);
            return (true, AdminAuditAction.StaffDeleted, kind);
        });
    }

    public async Task<PagedResult<AdminAuditDto>> ListAuditAsync(int page, int pageSize, CancellationToken ct)
    {
        var actor = await RequireActorAsync();
        var actorRoles = await users.GetRolesAsync(actor);
        var accountAdminOnly = actorRoles.Contains(Roles.AccountAdmin) && !actorRoles.Contains(Roles.Owner);

        var q = db.AdminAuditEntries.AsNoTracking();
        if (accountAdminOnly)
            q = q.Where(e => e.TargetKind == Roles.Manager);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new AdminAuditDto(e.Id, e.OccurredAt, e.ActorEmail, e.TargetEmail, e.Action, e.TargetKind))
            .ToListAsync(ct);
        return new PagedResult<AdminAuditDto>(items, total, page, pageSize);
    }

    private async Task MutateAsync(
        Guid id,
        CancellationToken ct,
        Func<AppUser, IList<string>, AppUser, string, Task<(bool WriteAudit, AdminAuditAction Action, string Kind)>> act)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ownerRoleId = await db.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUserRoles" WHERE "RoleId" = {ownerRoleId} FOR UPDATE""", ct);

        var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == current.UserId, ct)
                    ?? throw new UnauthorizedAccessException();
        var actorRoles = await RoleNamesAsync(actor.Id, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw AppException.NotFound();
        var targetRoles = await RoleNamesAsync(user.Id, ct);
        var kind = Roles.StaffKind(targetRoles) ?? throw AppException.NotFound();

        var (writeAudit, action, auditKind) = await act(actor, actorRoles, user, kind);
        if (writeAudit)
            AddAudit(actor, user, user.Email, action, auditKind, "{}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private Task<List<string>> RoleNamesAsync(Guid userId, CancellationToken ct) =>
        (from ur in db.UserRoles
         join r in db.Roles on ur.RoleId equals r.Id
         where ur.UserId == userId && r.Name != null
         select r.Name!).ToListAsync(ct);

    private static void ForbidIfProtectedFromAccountAdmin(IList<string> actorRoles, string kind)
    {
        var accountAdminOnly = actorRoles.Contains(Roles.AccountAdmin) && !actorRoles.Contains(Roles.Owner);
        if (accountAdminOnly && Roles.IsProtectedFromAccountAdmin([kind]))
            throw new AppException(StatusCodes.Status403Forbidden, ErrorCodes.ProtectedAccount);
    }

    private async Task EnsureNotLastEffectiveOwnerAsync(Guid targetId, CancellationToken ct)
    {
        var now = time.UtcNow;
        var ownerRoleId = await db.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync(ct);
        var effective = await (
            from u in db.Users
            join ur in db.UserRoles on u.Id equals ur.UserId
            where ur.RoleId == ownerRoleId
                  && (!u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd <= now)
            select u.Id).Distinct().ToListAsync(ct);
        if (effective.Count == 0 || (effective.Count == 1 && effective.Contains(targetId)))
            throw AppException.Conflict(ErrorCodes.LastOwner);
    }

    private void AddAudit(AppUser actor, AppUser target, string? targetEmail, AdminAuditAction action, string kind, string detailsJson)
    {
        db.AdminAuditEntries.Add(new AdminAuditEntry
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = time.UtcNow,
            ActorUserId = actor.Id,
            ActorEmail = actor.Email,
            TargetUserId = target.Id,
            TargetEmail = targetEmail,
            Action = action,
            TargetKind = kind,
            DetailsJson = detailsJson
        });
    }

    private async Task<AppUser> RequireActorAsync()
    {
        var actor = await users.FindByIdAsync(current.UserId.ToString());
        return actor ?? throw new UnauthorizedAccessException();
    }

    private bool IsLocked(AppUser user) =>
        user.LockoutEnabled && user.LockoutEnd is { } end && end > time.UtcNow;

    private static AppException IdentityFailure(IdentityResult result)
    {
        var password = result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal));
        if (password) return AppException.Validation("password", FieldCodes.PasswordWeak);
        var duplicate = result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName");
        if (duplicate) return AppException.Validation("email", FieldCodes.Duplicate);
        return AppException.Validation("email", FieldCodes.Invalid);
    }
}
