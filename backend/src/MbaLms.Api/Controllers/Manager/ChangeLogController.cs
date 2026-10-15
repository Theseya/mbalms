using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record ChangeLogFieldDto(string Field, JsonElement? Old, JsonElement? New);

public record ChangeLogListItemDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? ActorEmail,
    bool ActorIsSystem,
    bool ActorMissing,
    ChangeLogAction Action,
    ChangeLogEntityType EntityType,
    Guid EntityId,
    string EntityTitle,
    bool EntityExists,
    IReadOnlyList<string> GroupNames,
    string? Source,
    List<ChangeLogFieldDto> Changes);

public record ChangeLogAuthorDto(Guid Id, string Email);

[Route("api/manager/change-log")]
[ApiController]
public class ChangeLogController(AppDbContext db) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<PagedResult<ChangeLogListItemDto>> List(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? actorUserId,
        [FromQuery] ChangeLogAction? action,
        [FromQuery] ChangeLogEntityType? entityType,
        [FromQuery] [Range(1, int.MaxValue, ErrorMessage = FieldCodes.Range)] int page = 1,
        [FromQuery] [Range(1, Paging.MaxPageSize, ErrorMessage = FieldCodes.Range)] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var q = Db.ChangeLogEntries.AsNoTracking().AsQueryable();
        if (from is not null) q = q.Where(e => e.OccurredAt >= from);
        if (to is not null) q = q.Where(e => e.OccurredAt <= to);
        if (actorUserId is not null) q = q.Where(e => e.ActorUserId == actorUserId);
        if (action is not null) q = q.Where(e => e.Action == action);
        if (entityType is not null) q = q.Where(e => e.EntityType == entityType);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var actorIds = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var emails = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await Db.Users.AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? "", ct);

        var announcementIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Announcement)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingAnnouncements = announcementIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Announcements.AsNoTracking()
                .Where(a => announcementIds.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(ct)).ToHashSet();

        var lessonIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Lesson)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingLessons = lessonIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Lessons.AsNoTracking()
                .Where(l => lessonIds.Contains(l.Id))
                .Select(l => l.Id)
                .ToListAsync(ct)).ToHashSet();

        var surveyIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Survey)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingSurveys = surveyIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Surveys.AsNoTracking()
                .Where(s => surveyIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct)).ToHashSet();

        var templateIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.SurveyTemplate)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingTemplates = templateIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.SurveyTemplates.AsNoTracking()
                .Where(t => templateIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct)).ToHashSet();

        var gradeIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Grade)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingGrades = gradeIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Grades.AsNoTracking()
                .Where(g => gradeIds.Contains(g.Id))
                .Select(g => g.Id)
                .ToListAsync(ct)).ToHashSet();

        var groupIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Group)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingGroups = groupIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Groups.AsNoTracking()
                .Where(g => groupIds.Contains(g.Id))
                .Select(g => g.Id)
                .ToListAsync(ct)).ToHashSet();

        var disciplineIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Discipline)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingDisciplines = disciplineIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Disciplines.AsNoTracking()
                .Where(d => disciplineIds.Contains(d.Id))
                .Select(d => d.Id)
                .ToListAsync(ct)).ToHashSet();

        var periodIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.AcademicPeriod)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingPeriods = periodIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Periods.AsNoTracking()
                .Where(p => periodIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct)).ToHashSet();

        var teacherIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Teacher)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingTeachers = teacherIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Teachers.AsNoTracking()
                .Where(t => teacherIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct)).ToHashSet();

        var studentIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.Student)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingStudents = studentIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.Students.AsNoTracking()
                .Where(s => studentIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct)).ToHashSet();

        var sheetIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.WorkSheet)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingSheets = sheetIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.WorkSheets.AsNoTracking()
                .Where(s => sheetIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct)).ToHashSet();

        var elementIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.AssessmentElement)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingElements = elementIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.AssessmentElements.AsNoTracking()
                .Where(x => elementIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(ct)).ToHashSet();

        var categoryIds = rows
            .Where(r => r.EntityType == ChangeLogEntityType.AssessmentCategory)
            .Select(r => r.EntityId)
            .Distinct()
            .ToList();
        var existingCategories = categoryIds.Count == 0
            ? new HashSet<Guid>()
            : (await Db.AssessmentCategories.AsNoTracking()
                .Where(x => categoryIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(ct)).ToHashSet();

        var items = rows.Select(e =>
        {
            var ctx = ParseContext(e.ContextJson);
            var actorId = e.ActorUserId;
            var isSystem = actorId is null;
            string? actorEmail;
            var actorMissing = false;
            if (isSystem)
            {
                actorEmail = null;
            }
            else if (emails.TryGetValue(actorId!.Value, out var live))
            {
                actorEmail = live;
            }
            else
            {
                actorEmail = ctx.ActorEmail;
                actorMissing = true;
            }

            var exists = e.EntityType switch
            {
                ChangeLogEntityType.Announcement => existingAnnouncements.Contains(e.EntityId),
                ChangeLogEntityType.Lesson => existingLessons.Contains(e.EntityId),
                ChangeLogEntityType.Survey => existingSurveys.Contains(e.EntityId),
                ChangeLogEntityType.SurveyTemplate => existingTemplates.Contains(e.EntityId),
                ChangeLogEntityType.Grade => existingGrades.Contains(e.EntityId),
                ChangeLogEntityType.Group => existingGroups.Contains(e.EntityId),
                ChangeLogEntityType.Discipline => existingDisciplines.Contains(e.EntityId),
                ChangeLogEntityType.AcademicPeriod => existingPeriods.Contains(e.EntityId),
                ChangeLogEntityType.Teacher => existingTeachers.Contains(e.EntityId),
                ChangeLogEntityType.Student => existingStudents.Contains(e.EntityId),
                ChangeLogEntityType.WorkSheet => existingSheets.Contains(e.EntityId),
                ChangeLogEntityType.AssessmentElement => existingElements.Contains(e.EntityId),
                ChangeLogEntityType.AssessmentCategory => existingCategories.Contains(e.EntityId),
                _ => false,
            };
            return new ChangeLogListItemDto(
                e.Id, e.OccurredAt, actorId, actorEmail, isSystem, actorMissing,
                e.Action, e.EntityType, e.EntityId,
                ctx.Title, exists, ctx.GroupNames, ctx.Source, ParseChanges(e.ChangesJson));
        }).ToList();

        return new PagedResult<ChangeLogListItemDto>(items, total, page, pageSize);
    }

    /// <summary>Authors that already appear in the log (for filter dropdown).</summary>
    [HttpGet("authors")]
    public async Task<List<ChangeLogAuthorDto>> Authors(CancellationToken ct)
    {
        var ids = await Db.ChangeLogEntries.AsNoTracking()
            .Where(e => e.ActorUserId != null)
            .Select(e => e.ActorUserId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (ids.Count == 0) return [];

        var live = await Db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Email = u.Email ?? u.UserName ?? "" })
            .ToListAsync(ct);
        var byId = live.ToDictionary(x => x.Id, x => x.Email);

        var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var contexts = await Db.ChangeLogEntries.AsNoTracking()
                .Where(e => e.ActorUserId != null && missing.Contains(e.ActorUserId!.Value))
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => new { e.ActorUserId, e.ContextJson })
                .ToListAsync(ct);
            foreach (var row in contexts)
            {
                var id = row.ActorUserId!.Value;
                if (byId.ContainsKey(id)) continue;
                var email = ParseContext(row.ContextJson).ActorEmail;
                if (!string.IsNullOrWhiteSpace(email)) byId[id] = email!;
            }
        }

        return byId
            .Select(kv => new ChangeLogAuthorDto(kv.Key, kv.Value))
            .OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ChangeLogContext ParseContext(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var actorEmail = root.TryGetProperty("actorEmail", out var a) && a.ValueKind is not JsonValueKind.Null
                ? a.GetString() : null;
            var source = root.TryGetProperty("source", out var s) && s.ValueKind is JsonValueKind.String
                ? s.GetString() : null;
            var names = new List<string>();
            if (root.TryGetProperty("groupNames", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    var n = item.GetString();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            return new ChangeLogContext(title, names, actorEmail, source);
        }
        catch (JsonException)
        {
            return new ChangeLogContext("", [], null);
        }
    }

    private static List<ChangeLogFieldDto> ParseChanges(string json)
    {
        try
        {
            var list = new List<ChangeLogFieldDto>();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var field = el.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "";
                JsonElement? oldVal = el.TryGetProperty("old", out var o) ? o.Clone() : null;
                JsonElement? newVal = el.TryGetProperty("new", out var n) ? n.Clone() : null;
                list.Add(new ChangeLogFieldDto(field, oldVal, newVal));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
