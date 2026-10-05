using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

[Route("api/manager/imports/grades")]
public class GradesImportController(AppDbContext db, CurrentUser current, ImportSessionStore sessions, AppTime time)
    : ManagerControllerBase(db)
{
    public const string Entity = "grades";

    private static readonly ImportColumnSpec[] Columns =
    [
        new("email", ["Email", "Email студента", "Student email"], Required: true),
        new("discipline", ["Дисциплина", "Discipline"], Required: true),
        new("period", ["Учебный период", "Academic period", "Period"], Required: true),
        new("value", ["Оценка", "Grade"], Required: true)
    ];

    private static readonly EmailAddressAttribute EmailValidator = new();

    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? lang = null)
    {
        var t = ExportText.For(lang);
        var bytes = ExcelExporter.Build(t["sheet.grades"], new ExcelColumn<object>[]
        {
            new(t["col.email"], _ => null),
            new(t["col.discipline"], _ => null),
            new(t["col.period"], _ => null),
            new(t["col.grade"], _ => null)
        }, []);
        return File(bytes, ExcelExporter.ContentType, $"grades_template_{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
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

        var students = await Db.Students.AsNoTracking().Include(s => s.Group)
            .Select(s => new StudentSnap(s.Id, s.Email, s.Group!.Status))
            .ToListAsync(ct);
        var disciplines = await Db.Disciplines.AsNoTracking().Select(d => new NameSnap(d.Id, d.Name)).ToListAsync(ct);
        var periods = await Db.Periods.AsNoTracking().Select(p => new NameSnap(p.Id, p.Name)).ToListAsync(ct);
        var grades = await Db.Grades.AsNoTracking()
            .Select(g => new GradeSnap(g.Id, g.StudentId, g.DisciplineId, g.PeriodId, g.Value, g.Status))
            .ToListAsync(ct);

        var payload = new GradeImportPayload();
        var fileKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in table.Rows)
        {
            var row = ParseRow(raw, table.SheetName);
            payload.Rows.Add(row);
            if (row.Errors.Count == 0)
            {
                var key = RowKey(row.Email, row.DisciplineName, row.PeriodName);
                fileKeys[key] = fileKeys.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count == 0))
        {
            var key = RowKey(row.Email, row.DisciplineName, row.PeriodName);
            if (fileKeys.GetValueOrDefault(key) > 1)
            {
                MarkConflict(row, "email", FieldCodes.Ambiguous);
                continue;
            }

            if (!ResolveRefs(row, students, disciplines, periods))
                continue;

            var matches = grades.Where(g =>
                g.StudentId == row.ResolvedStudentId
                && g.DisciplineId == row.ResolvedDisciplineId
                && g.PeriodId == row.ResolvedPeriodId).ToList();

            if (matches.Count > 1)
            {
                MarkConflict(row, "email", FieldCodes.Ambiguous);
                continue;
            }

            if (matches.Count == 0)
            {
                row.Action = ImportRowAction.Create;
                continue;
            }

            var existing = matches[0];
            if (existing.Status == GradeStatus.Published)
            {
                row.MatchedGradeId = existing.Id;
                row.Errors.Add(new ImportRowError("value", ErrorCodes.InvalidStatusTransition, null, row.RowNumber));
                continue;
            }

            row.Action = ImportRowAction.Update;
            row.MatchedGradeId = existing.Id;
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count > 0 && r.Action != ImportRowAction.Conflict))
            row.Action = ImportRowAction.Error;

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
        if (session.Payload is not GradeImportPayload payload)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        var writable = payload.Rows.Where(r => r.Action is ImportRowAction.Create or ImportRowAction.Update).ToList();
        if (writable.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportEmpty);

        var students = await Db.Students.Include(s => s.Group).ToListAsync(ct);
        var disciplines = await Db.Disciplines.ToListAsync(ct);
        var periods = await Db.Periods.ToListAsync(ct);
        var grades = await Db.Grades.ToListAsync(ct);

        var plannedCreates = new List<(GradeImportRow Row, Guid StudentId, Guid DisciplineId, Guid PeriodId)>();
        var plannedUpdates = new List<(GradeImportRow Row, Grade Entity)>();
        var skipped = payload.Rows.Count - writable.Count;

        foreach (var row in writable)
        {
            if (row.Value is < 0 or > 100)
                throw AppException.Validation("value", FieldCodes.Range);

            var student = ResolveStudent(row.Email, students)
                ?? throw AppException.Validation("email", FieldCodes.NotFound);
            if (student.Group!.Status == GroupStatus.Archived)
                throw AppException.Conflict(ErrorCodes.GroupArchived);

            var disciplineId = ResolveUnique(row.DisciplineName, disciplines.Select(d => (d.Id, d.Name)))
                ?? throw AppException.Validation("discipline", FieldCodes.Ambiguous);
            var periodId = ResolveUnique(row.PeriodName, periods.Select(p => (p.Id, p.Name)))
                ?? throw AppException.Validation("period", FieldCodes.Ambiguous);

            var matches = grades.Where(g =>
                g.StudentId == student.Id && g.DisciplineId == disciplineId && g.PeriodId == periodId).ToList();
            if (matches.Count > 1)
                throw AppException.Validation("email", FieldCodes.Ambiguous);

            if (row.Action == ImportRowAction.Create)
            {
                if (matches.Count != 0)
                    throw AppException.Conflict(ErrorCodes.Duplicate);
                if (plannedCreates.Any(c => c.StudentId == student.Id && c.DisciplineId == disciplineId && c.PeriodId == periodId))
                    throw AppException.Validation("email", FieldCodes.Ambiguous);
                plannedCreates.Add((row, student.Id, disciplineId, periodId));
            }
            else
            {
                var entity = matches.SingleOrDefault()
                    ?? throw AppException.Validation("email", FieldCodes.NotFound);
                if (row.MatchedGradeId is Guid id && id != entity.Id)
                    throw AppException.Validation("email", FieldCodes.Ambiguous);
                if (entity.Status == GradeStatus.Published)
                    throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
                if (entity.Value == row.Value)
                {
                    skipped++;
                    continue;
                }
                plannedUpdates.Add((row, entity));
            }
        }

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var (row, studentId, disciplineId, periodId) in plannedCreates)
            {
                var grade = new Grade
                {
                    Id = Guid.CreateVersion7(),
                    StudentId = studentId,
                    DisciplineId = disciplineId,
                    PeriodId = periodId,
                    Value = row.Value,
                    Status = GradeStatus.Draft,
                    UpdatedAt = time.UtcNow
                };
                Db.Grades.Add(grade);
                Record(grade, GradeChangeAction.Created, null, null);
            }

            foreach (var (row, entity) in plannedUpdates)
            {
                Record(entity, GradeChangeAction.Updated, entity.Value, entity.Status, newValue: row.Value);
                entity.Value = row.Value;
                entity.UpdatedAt = time.UtcNow;
                // Stay Draft; never publish or notify.
            }

            await Db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return new ImportConfirmResultDto(plannedCreates.Count, plannedUpdates.Count, skipped);
    }

    private static bool ResolveRefs(GradeImportRow row, List<StudentSnap> students, List<NameSnap> disciplines, List<NameSnap> periods)
    {
        var studentMatches = students.Where(s => string.Equals(s.Email, row.Email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (studentMatches.Count > 1)
        {
            MarkConflict(row, "email", FieldCodes.Ambiguous);
            return false;
        }
        if (studentMatches.Count == 0)
        {
            row.Errors.Add(new ImportRowError("email", FieldCodes.NotFound, null, row.RowNumber));
            return false;
        }
        if (studentMatches[0].GroupStatus == GroupStatus.Archived)
        {
            row.Errors.Add(new ImportRowError("email", ErrorCodes.GroupArchived, null, row.RowNumber));
            return false;
        }
        row.ResolvedStudentId = studentMatches[0].Id;

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

        var periodMatches = periods.Where(p => string.Equals(p.Name, row.PeriodName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (periodMatches.Count > 1)
        {
            MarkConflict(row, "period", FieldCodes.Ambiguous);
            return false;
        }
        if (periodMatches.Count == 0)
        {
            row.Errors.Add(new ImportRowError("period", FieldCodes.NotFound, null, row.RowNumber));
            return false;
        }
        row.ResolvedPeriodId = periodMatches[0].Id;
        return true;
    }

    private static GradeImportRow ParseRow(ExcelImportDataRow raw, string sheet)
    {
        var email = raw.Values.GetValueOrDefault("email")?.Trim() ?? string.Empty;
        var discipline = raw.Values.GetValueOrDefault("discipline")?.Trim() ?? string.Empty;
        var period = raw.Values.GetValueOrDefault("period")?.Trim() ?? string.Empty;
        var valueRaw = raw.Values.GetValueOrDefault("value");
        var errors = new List<ImportRowError>();

        if (string.IsNullOrWhiteSpace(email))
            errors.Add(new ImportRowError("email", FieldCodes.Required, sheet, raw.RowNumber, "email"));
        else if (email.Length > 256)
            errors.Add(new ImportRowError("email", FieldCodes.MaxLength, sheet, raw.RowNumber, "email"));
        else if (!EmailValidator.IsValid(email))
            errors.Add(new ImportRowError("email", FieldCodes.Email, sheet, raw.RowNumber, "email"));

        if (string.IsNullOrWhiteSpace(discipline))
            errors.Add(new ImportRowError("discipline", FieldCodes.Required, sheet, raw.RowNumber, "discipline"));
        if (string.IsNullOrWhiteSpace(period))
            errors.Add(new ImportRowError("period", FieldCodes.Required, sheet, raw.RowNumber, "period"));

        var value = 0;
        if (string.IsNullOrWhiteSpace(valueRaw))
            errors.Add(new ImportRowError("value", FieldCodes.Required, sheet, raw.RowNumber, "value"));
        else if (!TryParseGrade(valueRaw, out value))
            errors.Add(new ImportRowError("value", FieldCodes.Range, sheet, raw.RowNumber, "value"));

        return new GradeImportRow
        {
            RowNumber = raw.RowNumber,
            Email = email,
            DisciplineName = discipline,
            PeriodName = period,
            Value = value,
            Errors = errors
        };
    }

    private static bool TryParseGrade(string raw, out int value)
    {
        value = 0;
        var text = raw.Trim();
        // Excel may give "85" or "85.0"
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value is >= 0 and <= 100;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && Math.Abs(d - Math.Round(d)) < 1e-9)
        {
            value = (int)Math.Round(d);
            return value is >= 0 and <= 100;
        }
        return false;
    }

    private static Student? ResolveStudent(string email, IReadOnlyList<Student> students)
    {
        var matches = students.Where(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Guid? ResolveUnique(string name, IEnumerable<(Guid Id, string Name)> items)
    {
        var matches = items.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private void Record(Grade grade, GradeChangeAction action, int? oldValue, GradeStatus? oldStatus, int? newValue = null) =>
        Db.GradeHistory.Add(new GradeHistoryEntry
        {
            Id = Guid.CreateVersion7(),
            GradeId = grade.Id,
            StudentId = grade.StudentId,
            DisciplineId = grade.DisciplineId,
            PeriodId = grade.PeriodId,
            Action = action,
            OldValue = oldValue,
            NewValue = newValue ?? grade.Value,
            OldStatus = oldStatus,
            NewStatus = grade.Status,
            ChangedByUserId = current.UserId,
            ChangedAt = time.UtcNow
        });

    private static void MarkConflict(GradeImportRow row, string field, string code)
    {
        row.Action = ImportRowAction.Conflict;
        row.Errors.Add(new ImportRowError(field, code, null, row.RowNumber));
    }

    private static string RowKey(string email, string discipline, string period) =>
        $"{email.Trim().ToUpperInvariant()}|{discipline.Trim().ToUpperInvariant()}|{period.Trim().ToUpperInvariant()}";

    private static ImportPreviewDto ToPreview(string importId, GradeImportPayload payload, IReadOnlyList<ImportCellError> fileErrors) =>
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
                    ["email"] = r.Email,
                    ["discipline"] = r.DisciplineName,
                    ["period"] = r.PeriodName,
                    ["value"] = r.Errors.Any(e => e.Field == "value" && e.Code is FieldCodes.Required or FieldCodes.Range)
                        ? null
                        : r.Value.ToString(CultureInfo.InvariantCulture)
                },
                r.Errors)).ToList(),
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

    private sealed record StudentSnap(Guid Id, string Email, GroupStatus GroupStatus);
    private sealed record NameSnap(Guid Id, string Name);
    private sealed record GradeSnap(Guid Id, Guid StudentId, Guid DisciplineId, Guid PeriodId, int Value, GradeStatus Status);
}
