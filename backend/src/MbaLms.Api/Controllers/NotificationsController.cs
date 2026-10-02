using System.Text.Json;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers;

public record NotificationDto(Guid Id, NotificationType Type, JsonElement Payload, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController(AppDbContext db, CurrentUser current, AppTime time) : ControllerBase
{
    [HttpGet]
    public async Task<List<NotificationDto>> List([FromQuery] bool unreadOnly = false, [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var userId = current.UserId;
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);
        var rows = await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(n => new NotificationDto(n.Id, n.Type, JsonDocument.Parse(n.PayloadJson).RootElement.Clone(),
            n.CreatedAt, n.ReadAt)).ToList();
    }

    [HttpGet("unread-count")]
    public async Task<object> UnreadCount(CancellationToken ct)
    {
        var userId = current.UserId;
        return new { count = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct) };
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var userId = current.UserId;
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)
                ?? throw AppException.NotFound();
        n.ReadAt ??= time.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var userId = current.UserId;
        var now = time.UtcNow;
        await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return NoContent();
    }
}
