using System.Globalization;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>Summary notification after a schedule import that actually changed data for the group.</summary>
public record ScheduleImportChangedPayload(string Change, int Created, int Updated);

[Route("api/manager/imports/lessons")]
public class LessonsImportController(AppDbContext db, CurrentUser current, ImportSessionStore sessions, AppTime time,
    NotificationService notifications)
    : ManagerControllerBase(db)
{
    public const string Entity = "lessons";
    private const int LocationMax = 500;
    private const int CommentMax = 2000;

    private static readonly ImportColumnSpec[] Columns =
    [
        new("lessonId", ["LessonId", "ID занятия"]),
        new("date", ["Дата", "Date"], Required: true),
        new("start", ["Начало", "Start"], Required: true),
        new("end", ["Окончание", "End"], Required: true),
        new("group", ["Группа", "Group"], Required: true),
        new("discipline", ["Дисциплина", "Discipline"], Required: true),
        new("teacher", ["Преподаватель", "Teacher"], Required: true),
        new("format", ["Формат", "Format"]),
        new("location", ["Место / ссылка", "Location / link", "Место", "Location"]),
        new("comment", ["Комментарий", "Comment"]),
        new("status", ["Статус занятия", "Lesson status", "Статус", "Status"])
    ];

    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? lang = null)
    {
        var t = ExportText.For(lang);
        var zone = $" ({time.TimeZoneId})";
        var bytes = ExcelExporter.Build(t["sheet.schedule"], new ExcelColumn<object>[]
        {
            new(t["col.lessonId"], _ => null),
            new(t["col.date"], _ => null),
            new(t["col.start"] + zone, _ => null),
            new(t["col.end"] + zone, _ => null),
            new(t["col.group"], _ => null),
            new(t["col.discipline"], _ => null),
            new(t["col.teacher"], _ => null),
            new(t["col.format"], _ => null),
            new(t["col.location"], _ => null),
            new(t["col.comment"], _ => null),
            new(t["col.lessonStatus"], _ => null)
        }, []);
        return File(bytes, ExcelExporter.ContentType, $"schedule_template_{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
    }

    [HttpPost("preview")]
    [RequestSizeLimit(2_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_000_000)]
    public async Task<ImportPreviewDto> Preview(IFormFile? file, CancellationToken ct)
    {
        await using var stream = await OpenUploadAsync(file);
        var table = ExcelImportReader.Read(stream, Columns);
        if (table.CellErrors.Count > 0)
        {
            return new ImportPreviewDto(
                ImportId: string.Empty,
                CreateCount: 0, UpdateCount: 0, ConflictCount: 0, ErrorCount: table.CellErrors.Count,
                Rows: [],
                FileErrors: table.CellErrors);
        }

        var groups = await Db.Groups.AsNoTracking().Select(g => new RefSnap(g.Id, g.Name, g.Status == GroupStatus.Active)).ToListAsync(ct);
        var disciplines = await Db.Disciplines.AsNoTracking().Select(d => new NameSnap(d.Id, d.Name)).ToListAsync(ct);
        var teachers = await Db.Teachers.AsNoTracking()
            .Select(t => new TeacherSnap(t.Id, t.LastName, t.FirstName, t.MiddleName)).ToListAsync(ct);
        var lessons = await Db.Lessons.AsNoTracking()
            .Select(l => new LessonSnap(l.Id, l.GroupId, l.TeacherId, l.StartsAt, l.EndsAt, l.Status)).ToListAsync(ct);

        var payload = new LessonImportPayload();
        var fileLessonIdCounts = new Dictionary<Guid, int>();

        foreach (var raw in table.Rows)
        {
            var row = ParseRow(raw, table.SheetName);
            payload.Rows.Add(row);
            if (row.Errors.Count == 0 && row.LessonId is Guid id)
                fileLessonIdCounts[id] = fileLessonIdCounts.GetValueOrDefault(id) + 1;
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count == 0))
        {
            if (row.LessonId is Guid lid && fileLessonIdCounts.GetValueOrDefault(lid) > 1)
            {
                MarkConflict(row, "lessonId", FieldCodes.Ambiguous);
                continue;
            }

            if (!ResolveRefs(row, groups, disciplines, teachers))
                continue;

            if (row.LessonId is Guid lessonId)
            {
                var existing = lessons.SingleOrDefault(l => l.Id == lessonId);
                if (existing is null)
                {
                    row.Errors.Add(new ImportRowError("lessonId", FieldCodes.NotFound, null, row.RowNumber));
                    continue;
                }
                row.Action = ImportRowAction.Update;
            }
            else
            {
                row.Action = ImportRowAction.Create;
            }
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count > 0 && r.Action != ImportRowAction.Conflict))
            row.Action = ImportRowAction.Error;

        AttachOverlapWarnings(payload.Rows, lessons);

        var importId = sessions.Save(current.UserId, Entity, payload);
        return ToPreview(importId, payload, []);
    }

    [HttpPost("confirm")]
    public async Task<ImportConfirmResultDto> Confirm([FromBody] ImportConfirmRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ImportId))
            throw AppException.Validation(nameof(request.ImportId), FieldCodes.Required);

        var session = sessions.Take(request.ImportId.Trim(), current.UserId, Entity);
        if (session is null)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);
        if (session.Payload is not LessonImportPayload payload)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        var writable = payload.Rows.Where(r => r.Action is ImportRowAction.Create or ImportRowAction.Update).ToList();
        if (writable.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportEmpty);

        var groups = await Db.Groups.ToListAsync(ct);
        var disciplines = await Db.Disciplines.ToListAsync(ct);
        var teachers = await Db.Teachers.ToListAsync(ct);
        var lessons = await Db.Lessons.ToListAsync(ct);

        var plannedCreates = new List<(LessonImportRow Row, Guid GroupId, Guid DisciplineId, Guid TeacherId, DateTimeOffset Starts, DateTimeOffset Ends)>();
        var plannedUpdates = new List<(LessonImportRow Row, Lesson Entity, Guid GroupId, Guid DisciplineId, Guid TeacherId, DateTimeOffset Starts, DateTimeOffset Ends, Guid OldGroupId)>();

        foreach (var row in writable)
        {
            RevalidateFields(row);
            var groupId = ResolveActiveGroupId(row.GroupName, groups)
                ?? throw AppException.Validation("group", FieldCodes.Ambiguous);
            var disciplineId = ResolveUniqueName(row.DisciplineName, disciplines.Select(d => (d.Id, d.Name)))
                ?? throw AppException.Validation("discipline", FieldCodes.Ambiguous);
            var teacherId = ResolveTeacherId(row.TeacherName, teachers)
                ?? throw AppException.Validation("teacher", FieldCodes.Ambiguous);

            var starts = time.ToUtc(row.StartsAtLocal!.Value, "startsAt");
            var ends = time.ToUtc(row.EndsAtLocal!.Value, "endsAt");
            if (ends <= starts) throw AppException.Validation("endsAt", FieldCodes.EndBeforeStart);
            if (row.EndsAtLocal.Value.Date != row.StartsAtLocal.Value.Date)
                throw AppException.Validation("endsAt", FieldCodes.NotSameDay);

            if (row.Action == ImportRowAction.Create)
            {
                if (row.LessonId is not null) throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
                plannedCreates.Add((row, groupId, disciplineId, teacherId, starts, ends));
            }
            else
            {
                var entity = lessons.SingleOrDefault(l => l.Id == row.LessonId)
                    ?? throw AppException.Validation("lessonId", FieldCodes.NotFound);
                await RequireActiveGroupAsync(entity.GroupId, "group", ct);
                plannedUpdates.Add((row, entity, groupId, disciplineId, teacherId, starts, ends, entity.GroupId));
            }
        }

        var groupStats = new Dictionary<Guid, (int Created, int Updated)>();
        var created = 0;
        var updated = 0;
        var skipped = payload.Rows.Count - writable.Count;

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var (row, groupId, disciplineId, teacherId, starts, ends) in plannedCreates)
            {
                Db.Lessons.Add(new Lesson
                {
                    Id = Guid.CreateVersion7(),
                    GroupId = groupId,
                    DisciplineId = disciplineId,
                    TeacherId = teacherId,
                    StartsAt = starts,
                    EndsAt = ends,
                    Format = row.Format,
                    Location = Clean(row.Location),
                    Comment = Clean(row.Comment),
                    Status = row.Status
                });
                created++;
                Bump(groupStats, groupId, create: true);
            }

            foreach (var (row, entity, groupId, disciplineId, teacherId, starts, ends, oldGroupId) in plannedUpdates)
            {
                var modified = !SameLesson(entity, groupId, disciplineId, teacherId, starts, ends, row);
                if (!modified)
                {
                    skipped++;
                    continue;
                }

                entity.GroupId = groupId;
                entity.DisciplineId = disciplineId;
                entity.TeacherId = teacherId;
                entity.StartsAt = starts;
                entity.EndsAt = ends;
                entity.Format = row.Format;
                entity.Location = Clean(row.Location);
                entity.Comment = Clean(row.Comment);
                entity.Status = row.Status;
                updated++;
                Bump(groupStats, groupId, create: false);
                if (oldGroupId != groupId)
                    Bump(groupStats, oldGroupId, create: false);
            }

            foreach (var (groupId, stats) in groupStats)
            {
                if (stats.Created == 0 && stats.Updated == 0) continue;
                await notifications.NotifyGroupAsync(groupId, NotificationType.ScheduleChanged,
                    new ScheduleImportChangedPayload("import", stats.Created, stats.Updated), ct);
            }

            await Db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return new ImportConfirmResultDto(created, updated, skipped);
    }

    private void AttachOverlapWarnings(List<LessonImportRow> rows, List<LessonSnap> dbLessons)
    {
        var planned = rows.Where(r =>
            r.Action is ImportRowAction.Create or ImportRowAction.Update
            && r.Status == LessonStatus.Scheduled
            && r.StartsAtLocal is not null && r.EndsAtLocal is not null
            && r.ResolvedGroupId is not null && r.ResolvedTeacherId is not null).ToList();

        foreach (var row in planned)
        {
            var starts = time.ToUtc(row.StartsAtLocal!.Value, "startsAt");
            var ends = time.ToUtc(row.EndsAtLocal!.Value, "endsAt");
            if (ends <= starts) continue;

            var dbHits = dbLessons.Where(l =>
                l.Status == LessonStatus.Scheduled
                && l.StartsAt < ends && l.EndsAt > starts
                && (l.GroupId == row.ResolvedGroupId || l.TeacherId == row.ResolvedTeacherId)
                && (row.LessonId is null || l.Id != row.LessonId)).ToList();

            var fileHits = planned.Where(o =>
                o.RowNumber != row.RowNumber
                && o.StartsAtLocal is not null && o.EndsAtLocal is not null
                && time.ToUtc(o.StartsAtLocal.Value, "startsAt") < ends
                && time.ToUtc(o.EndsAtLocal.Value, "endsAt") > starts
                && (o.ResolvedGroupId == row.ResolvedGroupId || o.ResolvedTeacherId == row.ResolvedTeacherId)).ToList();

            if (dbHits.Count == 0 && fileHits.Count == 0) continue;
            row.Warnings.Add(new ImportRowWarning("overlap",
                $"db:{dbHits.Count};file:{fileHits.Count}"));
        }
    }

    private static bool ResolveRefs(LessonImportRow row, List<RefSnap> groups, List<NameSnap> disciplines, List<TeacherSnap> teachers)
    {
        var groupMatches = groups.Where(g => string.Equals(g.Name, row.GroupName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (groupMatches.Count > 1)
        {
            MarkConflict(row, "group", FieldCodes.Ambiguous);
            return false;
        }
        if (groupMatches.Count == 0)
        {
            row.Errors.Add(new ImportRowError("group", FieldCodes.NotFound, null, row.RowNumber));
            return false;
        }
        if (!groupMatches[0].IsActive)
        {
            row.Errors.Add(new ImportRowError("group", ErrorCodes.GroupArchived, null, row.RowNumber));
            return false;
        }
        row.ResolvedGroupId = groupMatches[0].Id;

        var discMatches = disciplines.Where(d => string.Equals(d.Name, row.DisciplineName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (discMatches.Count > 1)
        {
            MarkConflict(row, "discipline", FieldCodes.Ambiguous);
            return false;
        }
        if (discMatches.Count == 0)
        {
            row.Errors.Add(new ImportRowError("discipline", FieldCodes.NotFound, null, row.RowNumber));
            return false;
        }
        row.ResolvedDisciplineId = discMatches[0].Id;

        var teacherMatches = teachers.Where(t => string.Equals(TeacherFullName(t), row.TeacherName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (teacherMatches.Count > 1)
        {
            MarkConflict(row, "teacher", FieldCodes.Ambiguous);
            return false;
        }
        if (teacherMatches.Count == 0)
        {
            row.Errors.Add(new ImportRowError("teacher", FieldCodes.NotFound, null, row.RowNumber));
            return false;
        }
        row.ResolvedTeacherId = teacherMatches[0].Id;
        return true;
    }

    private LessonImportRow ParseRow(ExcelImportDataRow raw, string sheet)
    {
        var lessonIdRaw = raw.Values.GetValueOrDefault("lessonId");
        var dateRaw = raw.Values.GetValueOrDefault("date");
        var startRaw = raw.Values.GetValueOrDefault("start");
        var endRaw = raw.Values.GetValueOrDefault("end");
        var group = raw.Values.GetValueOrDefault("group")?.Trim() ?? string.Empty;
        var discipline = raw.Values.GetValueOrDefault("discipline")?.Trim() ?? string.Empty;
        var teacher = raw.Values.GetValueOrDefault("teacher")?.Trim() ?? string.Empty;
        var formatRaw = raw.Values.GetValueOrDefault("format");
        var location = raw.Values.GetValueOrDefault("location");
        var comment = raw.Values.GetValueOrDefault("comment");
        var statusRaw = raw.Values.GetValueOrDefault("status");
        var errors = new List<ImportRowError>();

        Guid? lessonId = null;
        if (!string.IsNullOrWhiteSpace(lessonIdRaw))
        {
            if (!Guid.TryParse(lessonIdRaw.Trim(), out var id))
                errors.Add(new ImportRowError("lessonId", FieldCodes.Invalid, sheet, raw.RowNumber, "lessonId"));
            else
                lessonId = id;
        }

        if (string.IsNullOrWhiteSpace(dateRaw))
            errors.Add(new ImportRowError("date", FieldCodes.Required, sheet, raw.RowNumber, "date"));
        if (string.IsNullOrWhiteSpace(startRaw))
            errors.Add(new ImportRowError("start", FieldCodes.Required, sheet, raw.RowNumber, "start"));
        if (string.IsNullOrWhiteSpace(endRaw))
            errors.Add(new ImportRowError("end", FieldCodes.Required, sheet, raw.RowNumber, "end"));
        if (string.IsNullOrWhiteSpace(group))
            errors.Add(new ImportRowError("group", FieldCodes.Required, sheet, raw.RowNumber, "group"));
        if (string.IsNullOrWhiteSpace(discipline))
            errors.Add(new ImportRowError("discipline", FieldCodes.Required, sheet, raw.RowNumber, "discipline"));
        if (string.IsNullOrWhiteSpace(teacher))
            errors.Add(new ImportRowError("teacher", FieldCodes.Required, sheet, raw.RowNumber, "teacher"));

        var loc = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        if (loc is { Length: > LocationMax })
            errors.Add(new ImportRowError("location", FieldCodes.MaxLength, sheet, raw.RowNumber, "location"));
        var com = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (com is { Length: > CommentMax })
            errors.Add(new ImportRowError("comment", FieldCodes.MaxLength, sheet, raw.RowNumber, "comment"));

        DateTime? startsLocal = null;
        DateTime? endsLocal = null;
        if (!string.IsNullOrWhiteSpace(dateRaw) && !string.IsNullOrWhiteSpace(startRaw) && !string.IsNullOrWhiteSpace(endRaw))
        {
            if (!TryParseDate(dateRaw, out var date))
                errors.Add(new ImportRowError("date", FieldCodes.Invalid, sheet, raw.RowNumber, "date"));
            else if (!TryParseTime(startRaw, out var start))
                errors.Add(new ImportRowError("start", FieldCodes.Invalid, sheet, raw.RowNumber, "start"));
            else if (!TryParseTime(endRaw, out var end))
                errors.Add(new ImportRowError("end", FieldCodes.Invalid, sheet, raw.RowNumber, "end"));
            else
            {
                startsLocal = date.ToDateTime(start);
                endsLocal = date.ToDateTime(end);
                if (endsLocal <= startsLocal)
                    errors.Add(new ImportRowError("end", FieldCodes.EndBeforeStart, sheet, raw.RowNumber, "end"));
            }
        }

        LessonFormat? format = null;
        if (!string.IsNullOrWhiteSpace(formatRaw) && !TryParseFormat(formatRaw, out format))
            errors.Add(new ImportRowError("format", FieldCodes.Invalid, sheet, raw.RowNumber, "format"));

        var status = LessonStatus.Scheduled;
        if (!string.IsNullOrWhiteSpace(statusRaw) && !TryParseStatus(statusRaw, out status))
            errors.Add(new ImportRowError("status", FieldCodes.Invalid, sheet, raw.RowNumber, "status"));

        return new LessonImportRow
        {
            RowNumber = raw.RowNumber,
            LessonId = lessonId,
            DateText = dateRaw?.Trim() ?? string.Empty,
            StartText = startRaw?.Trim() ?? string.Empty,
            EndText = endRaw?.Trim() ?? string.Empty,
            GroupName = group,
            DisciplineName = discipline,
            TeacherName = teacher,
            FormatText = string.IsNullOrWhiteSpace(formatRaw) ? null : formatRaw.Trim(),
            Location = loc,
            Comment = com,
            StatusText = string.IsNullOrWhiteSpace(statusRaw) ? null : statusRaw.Trim(),
            StartsAtLocal = startsLocal,
            EndsAtLocal = endsLocal,
            Format = format,
            Status = status,
            Errors = errors
        };
    }

    private void RevalidateFields(LessonImportRow row)
    {
        if (row.StartsAtLocal is null || row.EndsAtLocal is null
            || string.IsNullOrWhiteSpace(row.GroupName)
            || string.IsNullOrWhiteSpace(row.DisciplineName)
            || string.IsNullOrWhiteSpace(row.TeacherName)
            || row.Location is { Length: > LocationMax }
            || row.Comment is { Length: > CommentMax })
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
    }

    private static Guid? ResolveActiveGroupId(string name, IReadOnlyList<Group> groups)
    {
        var matches = groups.Where(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1 || matches[0].Status != GroupStatus.Active) return null;
        return matches[0].Id;
    }

    private static Guid? ResolveUniqueName(string name, IEnumerable<(Guid Id, string Name)> items)
    {
        var matches = items.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private static Guid? ResolveTeacherId(string fullName, IReadOnlyList<Teacher> teachers)
    {
        var matches = teachers.Where(t =>
            string.Equals(string.IsNullOrWhiteSpace(t.MiddleName) ? $"{t.LastName} {t.FirstName}" : $"{t.LastName} {t.FirstName} {t.MiddleName}",
                fullName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private static bool TryParseDate(string value, out DateOnly date)
    {
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        return DateOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseTime(string value, out TimeOnly time)
    {
        var v = value.Trim();
        if (TimeOnly.TryParseExact(v, ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return true;
        if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1))
        {
            time = TimeOnly.FromTimeSpan(ts);
            return true;
        }
        // Excel serial fraction of day as invariant number
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d < 1)
        {
            time = TimeOnly.FromTimeSpan(TimeSpan.FromDays(d));
            return true;
        }
        time = default;
        return false;
    }

    private static bool TryParseFormat(string value, out LessonFormat? format)
    {
        format = null;
        var v = value.Trim();
        if (Enum.TryParse<LessonFormat>(v, ignoreCase: true, out var parsed))
        {
            format = parsed;
            return true;
        }
        format = v.ToLowerInvariant() switch
        {
            "очно" or "in person" => LessonFormat.Offline,
            "онлайн" or "online" => LessonFormat.Online,
            "гибрид" or "hybrid" => LessonFormat.Hybrid,
            _ => null
        };
        return format is not null;
    }

    private static bool TryParseStatus(string value, out LessonStatus status)
    {
        var v = value.Trim();
        if (Enum.TryParse(v, ignoreCase: true, out status)) return true;
        if (v.Equals("Запланировано", StringComparison.OrdinalIgnoreCase) || v.Equals("Scheduled", StringComparison.OrdinalIgnoreCase))
        {
            status = LessonStatus.Scheduled;
            return true;
        }
        if (v.Equals("Отменено", StringComparison.OrdinalIgnoreCase) || v.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            status = LessonStatus.Cancelled;
            return true;
        }
        status = default;
        return false;
    }

    private static void MarkConflict(LessonImportRow row, string field, string code)
    {
        row.Action = ImportRowAction.Conflict;
        row.Errors.Add(new ImportRowError(field, code, null, row.RowNumber));
    }

    private static void Bump(Dictionary<Guid, (int Created, int Updated)> stats, Guid groupId, bool create)
    {
        stats.TryGetValue(groupId, out var cur);
        stats[groupId] = create ? (cur.Created + 1, cur.Updated) : (cur.Created, cur.Updated + 1);
    }

    private static bool SameLesson(Lesson entity, Guid groupId, Guid disciplineId, Guid teacherId,
        DateTimeOffset starts, DateTimeOffset ends, LessonImportRow row) =>
        entity.GroupId == groupId
        && entity.DisciplineId == disciplineId
        && entity.TeacherId == teacherId
        && entity.StartsAt == starts
        && entity.EndsAt == ends
        && entity.Format == row.Format
        && string.Equals(entity.Location, Clean(row.Location), StringComparison.Ordinal)
        && string.Equals(entity.Comment, Clean(row.Comment), StringComparison.Ordinal)
        && entity.Status == row.Status;

    private static string TeacherFullName(TeacherSnap t) =>
        string.IsNullOrWhiteSpace(t.MiddleName) ? $"{t.LastName} {t.FirstName}" : $"{t.LastName} {t.FirstName} {t.MiddleName}";

    private static ImportPreviewDto ToPreview(string importId, LessonImportPayload payload, IReadOnlyList<ImportCellError> fileErrors) =>
        new(
            importId,
            payload.Rows.Count(r => r.Action == ImportRowAction.Create),
            payload.Rows.Count(r => r.Action == ImportRowAction.Update),
            payload.Rows.Count(r => r.Action == ImportRowAction.Conflict),
            payload.Rows.Count(r => r.Action == ImportRowAction.Error) + fileErrors.Count,
            payload.Rows.Select(r => new ImportPreviewRowDto(
                r.RowNumber,
                r.Action,
                new Dictionary<string, string?>
                {
                    ["lessonId"] = r.LessonId?.ToString(),
                    ["date"] = r.StartsAtLocal?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? r.DateText,
                    ["start"] = r.StartsAtLocal?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? r.StartText,
                    ["end"] = r.EndsAtLocal?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? r.EndText,
                    ["group"] = r.GroupName,
                    ["discipline"] = r.DisciplineName,
                    ["teacher"] = r.TeacherName,
                    ["format"] = r.Format?.ToString() ?? r.FormatText,
                    ["location"] = r.Location,
                    ["comment"] = r.Comment,
                    ["status"] = r.Status.ToString()
                },
                r.Errors,
                r.Warnings)).ToList(),
            fileErrors);

    private static async Task<Stream> OpenUploadAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
        var name = file.FileName ?? string.Empty;
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
        var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        return ms;
    }

    private sealed record RefSnap(Guid Id, string Name, bool IsActive);
    private sealed record NameSnap(Guid Id, string Name);
    private sealed record TeacherSnap(Guid Id, string LastName, string FirstName, string? MiddleName);
    private sealed record LessonSnap(Guid Id, Guid GroupId, Guid TeacherId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, LessonStatus Status);
}
