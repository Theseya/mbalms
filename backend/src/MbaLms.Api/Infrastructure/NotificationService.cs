using System.Text.Json;
using System.Text.Json.Serialization;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Infrastructure;

/// <summary>
/// Stores in-app notifications. Notifications are added to the current DbContext unit of work,
/// so they are committed atomically with the change that caused them.
/// Delivery channels (e.g. email) can be added later behind this service.
/// </summary>
public class NotificationService(AppDbContext db, AppTime time)
{
    /// <summary>Same conventions as API responses: camelCase names and enums as strings.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task NotifyGroupAsync(Guid groupId, NotificationType type, object payload, CancellationToken ct)
    {
        var userIds = await db.Students.Where(s => s.GroupId == groupId).Select(s => s.UserId).ToListAsync(ct);
        foreach (var userId in userIds) Add(userId, type, payload);
    }

    public void Add(Guid userId, NotificationType type, object payload)
    {
        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, PayloadOptions),
            CreatedAt = time.UtcNow
        });
    }
}
