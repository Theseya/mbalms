using System.Text.Json;
using System.Text.Json.Serialization;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;

namespace MbaLms.Api.Infrastructure;

/// <summary>One allow-listed field change. Long free-text (announcement body, lesson comment) uses markers only.</summary>
public record ChangeLogField(string Field, object? Old, object? New);

/// <param name="Source">Optional origin key stored in context (e.g. excelImport); null for ordinary UI edits.</param>
public record ChangeLogContext(string Title, IReadOnlyList<string> GroupNames, string? ActorEmail, string? Source = null);

/// <summary>
/// Appends change-log rows to the same DbContext as the business change (same SaveChanges).
/// Does not call SaveChanges itself.
/// </summary>
public class ChangeLogWriter(AppDbContext db, AppTime time, CurrentUser current)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Record an action attributed to the signed-in user.</summary>
    public void Add(
        ChangeLogEntityType entityType,
        Guid entityId,
        ChangeLogAction action,
        IReadOnlyList<ChangeLogField> changes,
        ChangeLogContext context) =>
        Append(current.UserId, entityType, entityId, action, changes, context);

    /// <summary>Record a true system action (ActorUserId null). Do not use for deleted users.</summary>
    public void AddSystem(
        ChangeLogEntityType entityType,
        Guid entityId,
        ChangeLogAction action,
        IReadOnlyList<ChangeLogField> changes,
        ChangeLogContext context) =>
        Append(null, entityType, entityId, action, changes, context with { ActorEmail = null });

    private void Append(
        Guid? actorUserId,
        ChangeLogEntityType entityType,
        Guid entityId,
        ChangeLogAction action,
        IReadOnlyList<ChangeLogField> changes,
        ChangeLogContext context)
    {
        db.ChangeLogEntries.Add(new ChangeLogEntry
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = time.UtcNow,
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ChangesJson = JsonSerializer.Serialize(changes, JsonOptions),
            ContextJson = JsonSerializer.Serialize(context, JsonOptions),
        });
    }
}
