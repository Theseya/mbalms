using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers;

/// <summary>
/// Shared student visibility and ordering for announcements (dashboard, list, detail).
/// </summary>
public static class AnnouncementQueries
{
    /// <summary>
    /// Published, not expired, targeted at the student's group. Expiry does not change Status in the DB.
    /// </summary>
    public static IQueryable<Announcement> VisibleToStudent(AppDbContext db, Guid groupId, DateTimeOffset now) =>
        db.Announcements.AsNoTracking()
            .Where(a => a.Status == AnnouncementStatus.Published
                        && (a.ExpiresAt == null || a.ExpiresAt > now)
                        && a.Groups.Any(g => g.GroupId == groupId));

    public static IOrderedQueryable<Announcement> Ordered(IQueryable<Announcement> q) =>
        q.OrderByDescending(a => a.IsPinned).ThenByDescending(a => a.PublishedAt);

    public static bool IsExpired(Announcement a, DateTimeOffset now) =>
        a.Status == AnnouncementStatus.Published && a.ExpiresAt is not null && a.ExpiresAt <= now;
}
