using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record AnnouncementGroupDto(Guid Id, string Name, GroupStatus Status);

public record AnnouncementListItemDto(
    Guid Id, string Title, AnnouncementStatus Status, bool IsPinned, bool IsExpired,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt,
    DateTime? ExpiresAtLocal, List<AnnouncementGroupDto> Groups);

public record AnnouncementDetailDto(
    Guid Id, string Title, string Body, AnnouncementStatus Status, bool IsPinned, bool IsExpired,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt,
    DateTime? ExpiresAtLocal, List<AnnouncementGroupDto> Groups);

public record AnnouncementRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(300, ErrorMessage = FieldCodes.MaxLength)] string Title,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(8000, ErrorMessage = FieldCodes.MaxLength)] string Body,
    bool IsPinned,
    DateTime? ExpiresAt,
    [Required(ErrorMessage = FieldCodes.Required)] [MinLength(1, ErrorMessage = FieldCodes.Required)]
    List<Guid> GroupIds);

[Route("api/manager/announcements")]
[ApiController]
public class AnnouncementsController(
    AppDbContext db, AppTime time, CurrentUser current, ChangeLogWriter changeLog, UserManager<AppUser> users)
    : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<AnnouncementListItemDto>> List(
        [FromQuery] Guid? groupId, [FromQuery] AnnouncementStatus? status, CancellationToken ct)
    {
        var now = time.UtcNow;
        var q = Db.Announcements.AsNoTracking().AsSplitQuery()
            .Include(a => a.Groups).ThenInclude(g => g.Group)
            .AsQueryable();
        if (groupId is not null)
            q = q.Where(a => a.Groups.Any(g => g.GroupId == groupId));
        if (status is not null)
            q = q.Where(a => a.Status == status);

        var rows = await q
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.PublishedAt ?? a.UpdatedAt)
            .ToListAsync(ct);

        return rows.Select(a => ToListItem(a, now)).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<AnnouncementDetailDto> Get(Guid id, CancellationToken ct) =>
        await LoadDetail(id, ct);

    [HttpPost]
    public async Task<ActionResult<AnnouncementDetailDto>> Create(AnnouncementRequest r, CancellationToken ct)
    {
        var now = time.UtcNow;
        var announcement = new Announcement
        {
            Id = Guid.CreateVersion7(),
            Title = r.Title.Trim(),
            Body = NormalizeBody(r.Body),
            Status = AnnouncementStatus.Draft,
            IsPinned = r.IsPinned,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = current.UserId,
            ExpiresAt = time.ToUtc(r.ExpiresAt, nameof(r.ExpiresAt)),
        };
        await SetGroupsAsync(announcement, r.GroupIds, existingIds: [], ct);

        var groupNames = await GroupNamesAsync(announcement.Groups.Select(g => g.GroupId), ct);
        var actorEmail = await ActorEmailAsync(ct);
        changeLog.Add(
            ChangeLogEntityType.Announcement,
            announcement.Id,
            ChangeLogAction.Created,
            AnnouncementChangeLog.ForCreate(announcement, groupNames),
            AnnouncementChangeLog.Context(announcement.Title, groupNames, actorEmail));

        Db.Announcements.Add(announcement);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = announcement.Id }, await LoadDetail(announcement.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<AnnouncementDetailDto> Update(Guid id, AnnouncementRequest r, CancellationToken ct)
    {
        var announcement = await Db.Announcements.Include(a => a.Groups)
                               .FirstOrDefaultAsync(a => a.Id == id, ct)
                           ?? throw AppException.NotFound();

        var previousExpires = announcement.ExpiresAt;
        var expires = time.ToUtc(r.ExpiresAt, nameof(r.ExpiresAt));
        if (announcement.Status == AnnouncementStatus.Published)
            RequireExpiresInFutureIfChanged(expires, previousExpires);

        var newTitle = r.Title.Trim();
        var newBody = NormalizeBody(r.Body);
        // Resolve target group names before mutating so validation (archived etc.) runs first.
        await EnsureGroupsValidAsync(r.GroupIds, announcement.Groups.Select(g => g.GroupId).ToHashSet(), ct);
        var newGroupIds = r.GroupIds.Distinct().ToList();
        var oldGroupNames = await GroupNamesAsync(announcement.Groups.Select(g => g.GroupId), ct);
        var newGroupNames = await GroupNamesAsync(newGroupIds, ct);

        var changes = AnnouncementChangeLog.Diff(
            announcement.Title, newTitle,
            announcement.Body, newBody,
            oldGroupNames, newGroupNames,
            announcement.IsPinned, r.IsPinned,
            announcement.ExpiresAt, expires,
            announcement.Status, announcement.Status);

        if (changes.Count == 0)
            return await LoadDetail(id, ct);

        announcement.Title = newTitle;
        announcement.Body = newBody;
        announcement.IsPinned = r.IsPinned;
        announcement.ExpiresAt = expires;
        announcement.UpdatedAt = time.UtcNow;
        ApplyGroups(announcement, newGroupIds);

        var actorEmail = await ActorEmailAsync(ct);
        changeLog.Add(
            ChangeLogEntityType.Announcement,
            announcement.Id,
            ChangeLogAction.Updated,
            changes,
            AnnouncementChangeLog.Context(announcement.Title, newGroupNames, actorEmail));

        await Db.SaveChangesAsync(ct);
        return await LoadDetail(id, ct);
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<AnnouncementDetailDto> Publish(Guid id, CancellationToken ct)
    {
        var announcement = await Db.Announcements.Include(a => a.Groups)
                               .FirstOrDefaultAsync(a => a.Id == id, ct)
                           ?? throw AppException.NotFound();

        if (announcement.Status == AnnouncementStatus.Published)
            return await LoadDetail(id, ct);

        if (announcement.Groups.Count == 0)
            throw AppException.Validation(nameof(AnnouncementRequest.GroupIds), FieldCodes.Required);

        RequireExpiresInFutureIfSet(announcement.ExpiresAt);

        var oldStatus = announcement.Status;
        announcement.Status = AnnouncementStatus.Published;
        announcement.PublishedAt = time.UtcNow;
        announcement.UpdatedAt = time.UtcNow;

        var groupNames = await GroupNamesAsync(announcement.Groups.Select(g => g.GroupId), ct);
        var actorEmail = await ActorEmailAsync(ct);
        changeLog.Add(
            ChangeLogEntityType.Announcement,
            announcement.Id,
            ChangeLogAction.Published,
            AnnouncementChangeLog.StatusOnly(oldStatus, AnnouncementStatus.Published),
            AnnouncementChangeLog.Context(announcement.Title, groupNames, actorEmail));

        await Db.SaveChangesAsync(ct);
        return await LoadDetail(id, ct);
    }

    [HttpPost("{id:guid}/unpublish")]
    public async Task<AnnouncementDetailDto> Unpublish(Guid id, CancellationToken ct)
    {
        var announcement = await Db.Announcements.Include(a => a.Groups)
                               .FirstOrDefaultAsync(a => a.Id == id, ct)
                           ?? throw AppException.NotFound();

        if (announcement.Status == AnnouncementStatus.Draft)
            throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        if (announcement.Status == AnnouncementStatus.Unpublished)
            return await LoadDetail(id, ct);

        var oldStatus = announcement.Status;
        announcement.Status = AnnouncementStatus.Unpublished;
        announcement.UpdatedAt = time.UtcNow;

        var groupNames = await GroupNamesAsync(announcement.Groups.Select(g => g.GroupId), ct);
        var actorEmail = await ActorEmailAsync(ct);
        changeLog.Add(
            ChangeLogEntityType.Announcement,
            announcement.Id,
            ChangeLogAction.Unpublished,
            AnnouncementChangeLog.StatusOnly(oldStatus, AnnouncementStatus.Unpublished),
            AnnouncementChangeLog.Context(announcement.Title, groupNames, actorEmail));

        await Db.SaveChangesAsync(ct);
        return await LoadDetail(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var announcement = await Db.Announcements.Include(a => a.Groups)
                               .FirstOrDefaultAsync(a => a.Id == id, ct)
                           ?? throw AppException.NotFound();
        if (announcement.Status == AnnouncementStatus.Published)
            throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);

        var groupNames = await GroupNamesAsync(announcement.Groups.Select(g => g.GroupId), ct);
        var actorEmail = await ActorEmailAsync(ct);
        changeLog.Add(
            ChangeLogEntityType.Announcement,
            announcement.Id,
            ChangeLogAction.Deleted,
            [
                new ChangeLogField("title", announcement.Title, null),
                new ChangeLogField("status", announcement.Status.ToString(), null),
                new ChangeLogField("groupIds", groupNames.ToList(), null),
            ],
            AnnouncementChangeLog.Context(announcement.Title, groupNames, actorEmail));

        Db.Announcements.Remove(announcement);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<AnnouncementDetailDto> LoadDetail(Guid id, CancellationToken ct)
    {
        var a = await Db.Announcements.AsNoTracking().AsSplitQuery()
                    .Include(x => x.Groups).ThenInclude(g => g.Group)
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound();
        return ToDetail(a, time.UtcNow);
    }

    private AnnouncementListItemDto ToListItem(Announcement a, DateTimeOffset now) =>
        new(a.Id, a.Title, a.Status, a.IsPinned, AnnouncementQueries.IsExpired(a, now),
            a.CreatedAt, a.UpdatedAt, a.PublishedAt, time.ToLocal(a.ExpiresAt), MapGroups(a));

    private AnnouncementDetailDto ToDetail(Announcement a, DateTimeOffset now) =>
        new(a.Id, a.Title, a.Body, a.Status, a.IsPinned, AnnouncementQueries.IsExpired(a, now),
            a.CreatedAt, a.UpdatedAt, a.PublishedAt, time.ToLocal(a.ExpiresAt), MapGroups(a));

    private static List<AnnouncementGroupDto> MapGroups(Announcement a) =>
        a.Groups
            .OrderBy(g => g.Group!.Name)
            .Select(g => new AnnouncementGroupDto(g.GroupId, g.Group!.Name, g.Group.Status))
            .ToList();

    private async Task EnsureGroupsValidAsync(List<Guid> groupIds, HashSet<Guid> existingIds, CancellationToken ct)
    {
        if (groupIds.Count == 0)
            throw AppException.Validation(nameof(AnnouncementRequest.GroupIds), FieldCodes.Required);

        var distinct = groupIds.Distinct().ToList();
        var found = await Db.Groups.Where(g => distinct.Contains(g.Id)).ToListAsync(ct);
        if (found.Count != distinct.Count)
            throw AppException.Validation(nameof(AnnouncementRequest.GroupIds), FieldCodes.NotFound);

        foreach (var group in found)
        {
            if (group.Status == GroupStatus.Archived && !existingIds.Contains(group.Id))
                throw AppException.Conflict(ErrorCodes.GroupArchived);
        }
    }

    private async Task SetGroupsAsync(Announcement announcement, List<Guid> groupIds, HashSet<Guid> existingIds,
        CancellationToken ct)
    {
        await EnsureGroupsValidAsync(groupIds, existingIds, ct);
        ApplyGroups(announcement, groupIds.Distinct().ToList());
    }

    private static void ApplyGroups(Announcement announcement, List<Guid> groupIds)
    {
        var keep = groupIds.ToHashSet();
        announcement.Groups.RemoveAll(g => !keep.Contains(g.GroupId));
        foreach (var id in groupIds.Where(id => announcement.Groups.All(g => g.GroupId != id)))
            announcement.Groups.Add(new AnnouncementGroup { AnnouncementId = announcement.Id, GroupId = id });
    }

    private async Task<List<string>> GroupNamesAsync(IEnumerable<Guid> groupIds, CancellationToken ct)
    {
        var ids = groupIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var map = await Db.Groups.AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return ids.Select(id => map.TryGetValue(id, out var n) ? n : id.ToString())
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string?> ActorEmailAsync(CancellationToken ct)
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }

    private void RequireExpiresInFutureIfSet(DateTimeOffset? expires)
    {
        if (expires is not null && expires <= time.UtcNow)
            throw AppException.Validation(nameof(AnnouncementRequest.ExpiresAt), FieldCodes.Invalid);
    }

    private void RequireExpiresInFutureIfChanged(DateTimeOffset? next, DateTimeOffset? previous)
    {
        if (next is null) return;
        if (previous is not null && previous == next) return;
        RequireExpiresInFutureIfSet(next);
    }

    private static string NormalizeBody(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0) throw AppException.Validation(nameof(AnnouncementRequest.Body), FieldCodes.Required);
        return trimmed;
    }
}
