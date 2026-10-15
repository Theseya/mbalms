using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record WorkSheetWorkDto(Guid Id, string Name);

public record WorkSheetCategoryDto(Guid Id, string Name, decimal Weight, AssessmentCategoryKind Kind,
    bool IncludeInAverage, bool LegacyManualAttendance, List<WorkSheetWorkDto> Works);

/// <summary>The gradebook grade of the student for the sheet's discipline and period.</summary>
public record WorkSheetGradeDto(Guid Id, int Value, GradeStatus Status);

/// <param name="InGroup">False for a student who has left the group; the row is kept read-only.</param>
/// <param name="Final">The manual final grade if set, otherwise the rounded calculated result.</param>
public record WorkSheetRowDto(Guid StudentId, string StudentName, bool InGroup, string? CurrentGroupName,
    Dictionary<Guid, decimal> Scores, Dictionary<Guid, decimal?> CategoryScores, int? AttendedLessons,
    decimal? AttendancePercent, decimal? AttendanceScore, decimal? AttendanceContribution, AttendanceStatus AttendanceStatus,
    CalculationState CalculationState, int MissingCount, decimal? CalculatedRaw, int? CalculatedRounded, int? ManualFinal,
    int? Final, int? TransferredFinal, DateTimeOffset? TransferredAt, WorkSheetGradeDto? Grade);

/// <param name="Id">Null until something is saved for the group, discipline and period.</param>
public record WorkSheetDto(Guid? Id, Guid GroupId, string GroupName, GroupStatus GroupStatus,
    Guid DisciplineId, string DisciplineName, Guid PeriodId, string PeriodName, int? TotalLessons,
    decimal? AttendanceThreshold, decimal AttendanceWeight, CalculationMode CalculationMode,
    bool IncludeAttendanceInAverage, List<string> AverageTerms, List<WorkSheetCategoryDto> Categories,
    decimal WeightTotal, bool WeightsComplete, bool ReadyForTransfer, bool HasTransfers, List<WorkSheetRowDto> Rows);

/// <summary>An existing sheet of a group and discipline, one per academic period.</summary>
public record WorkSheetSummaryDto(Guid Id, Guid GroupId, Guid DisciplineId, Guid PeriodId, string PeriodName,
    bool HasTransfers, DateTimeOffset UpdatedAt);

/// <summary>The period is part of the sheet's identity and cannot be changed here.</summary>
public record WorkSheetSettingsRequest([Range(0, 1000, ErrorMessage = FieldCodes.Range)] int? TotalLessons);

/// <summary>The complete state of one row; a missing or null score is an empty (not zero) score.</summary>
public record WorkSheetRowInput(
    [Required(ErrorMessage = FieldCodes.Required)] Guid? StudentId,
    Dictionary<Guid, decimal?>? Scores,
    [Range(0, 1000, ErrorMessage = FieldCodes.Range)] int? AttendedLessons,
    [Range(0, WorkSheetRules.MaxFinal, ErrorMessage = FieldCodes.Range)] int? ManualFinal);

public record WorkSheetRowsRequest([Required(ErrorMessage = FieldCodes.Required)] List<WorkSheetRowInput> Rows);

public enum TransferChange { New, Changed, Unchanged }

public enum TransferSkipReason { NoElements, WeightsIncomplete, MissingScores, NotInGroup }

/// <param name="Value">Gradebook value: the 0–10 final grade × 10.</param>
public record TransferItemDto(Guid StudentId, string StudentName, int Final, bool Manual, int Value,
    TransferChange Change, int? CurrentValue, GradeStatus? CurrentStatus);

public record TransferSkipDto(Guid StudentId, string StudentName, TransferSkipReason Reason, int MissingCount);

public record TransferPreviewDto(Guid PeriodId, string PeriodName, int Factor,
    List<TransferItemDto> Items, List<TransferSkipDto> Skipped);

/// <param name="Value">The value shown in the preview; a mismatch means the sheet changed meanwhile.</param>
public record TransferEntry(
    [Required(ErrorMessage = FieldCodes.Required)] Guid? StudentId,
    [Required(ErrorMessage = FieldCodes.Required)] [Range(0, 100, ErrorMessage = FieldCodes.Range)] int? Value);

/// <param name="ConfirmChanges">Must be true if any entry changes an existing grade.</param>
public record TransferRequest(
    [Required(ErrorMessage = FieldCodes.Required)] List<TransferEntry> Entries,
    bool ConfirmChanges = false);

public record TransferResultDto(int Created, int Updated, int Unchanged);

/// <summary>
/// Manager working sheet of a group, discipline and academic period: intermediate scores, attendance and the final grade.
/// Every period has its own sheet with its own results; assessment elements are shared by the discipline.
/// Saving never publishes anything; only <see cref="Transfer"/> writes final grades to the gradebook of the sheet's period.
/// </summary>
[Route("api/manager/worksheets/{groupId:guid}/{disciplineId:guid}/{periodId:guid}")]
public class WorkSheetsController(
    AppDbContext db, AppTime time, ChangeLogWriter changeLog, CurrentUser current, UserManager<AppUser> users,
    GradeWriter writer)
    : ManagerControllerBase(db)
{
    /// <summary>Existing sheets, optionally of one group and discipline, newest period first.</summary>
    [HttpGet("~/api/manager/worksheets")]
    public Task<List<WorkSheetSummaryDto>> List([FromQuery] Guid? groupId, [FromQuery] Guid? disciplineId, CancellationToken ct)
    {
        var q = Db.WorkSheets.AsNoTracking();
        if (groupId is not null) q = q.Where(s => s.GroupId == groupId);
        if (disciplineId is not null) q = q.Where(s => s.DisciplineId == disciplineId);
        return q.OrderByDescending(s => s.Period!.StartDate).ThenBy(s => s.Period!.Name)
            .Select(s => new WorkSheetSummaryDto(s.Id, s.GroupId, s.DisciplineId, s.PeriodId, s.Period!.Name,
                s.Rows.Any(r => r.TransferredAt != null), s.UpdatedAt))
            .ToListAsync(ct);
    }

    [HttpGet]
    public async Task<WorkSheetDto> Get(Guid groupId, Guid disciplineId, Guid periodId, CancellationToken ct)
    {
        var group = await Db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw AppException.NotFound();
        var discipline = await Db.Disciplines.AsNoTracking().FirstOrDefaultAsync(d => d.Id == disciplineId, ct)
                         ?? throw AppException.NotFound();
        var period = await Db.Periods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppException.NotFound();
        var categories = await CategoriesAsync(disciplineId, ct);
        var settings = await Db.DisciplineAssessmentSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DisciplineId == disciplineId, ct);
        var ctx = WorkSheetMath.ContextOf(settings);
        var threshold = settings?.AttendanceThreshold;
        var sheet = await FindSheet(groupId, disciplineId, periodId).AsNoTracking().FirstOrDefaultAsync(ct);
        var rows = sheet is null
            ? []
            : await Db.WorkSheetRows.AsNoTracking().Include(r => r.Scores).Include(r => r.Student).ThenInclude(s => s!.Group)
                .Where(r => r.WorkSheetId == sheet.Id).ToListAsync(ct);
        var students = await Db.Students.AsNoTracking().Where(s => s.GroupId == groupId)
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.MiddleName).ToListAsync(ct);

        var rowsByStudent = rows.ToDictionary(r => r.StudentId);
        var inGroup = students.Select(s => s.Id).ToHashSet();
        var moved = rows.Where(r => !inGroup.Contains(r.StudentId)).Select(r => r.Student!)
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.MiddleName).ToList();

        var ids = students.Select(s => s.Id).Concat(moved.Select(s => s.Id)).ToList();
        var grades = await Db.Grades.AsNoTracking()
            .Where(g => g.DisciplineId == disciplineId && g.PeriodId == periodId && ids.Contains(g.StudentId))
            .ToDictionaryAsync(g => g.StudentId, ct);
        var attendance = categories.FirstOrDefault(c => c.Kind == AssessmentCategoryKind.Attendance);

        WorkSheetRowDto RowDto(Student s, bool isInGroup)
        {
            var row = rowsByStudent.GetValueOrDefault(s.Id);
            var scores = WorkSheetMath.ScoresOf(row, categories);
            var calc = WorkSheetRules.Calculate(categories, scores, row?.AttendedLessons, sheet?.TotalLessons, ctx);
            var attendanceScore = WorkSheetRules.AttendanceScore(row?.AttendedLessons, sheet?.TotalLessons);
            var grade = grades.GetValueOrDefault(s.Id);
            return new WorkSheetRowDto(s.Id, s.FullName, isInGroup, isInGroup ? null : s.Group?.Name, scores,
                WorkSheetMath.CategoryScores(categories, scores),
                row?.AttendedLessons, WorkSheetRules.AttendancePercent(row?.AttendedLessons, sheet?.TotalLessons),
                attendanceScore,
                attendanceScore is decimal sc && attendance is { Weight: > 0 }
                    ? WorkSheetRules.Contribution(sc, attendance.Weight) : null,
                WorkSheetRules.Attendance(row?.AttendedLessons, sheet?.TotalLessons, threshold),
                calc.State, calc.MissingCount, calc.Raw, calc.Rounded, row?.ManualFinal, row?.ManualFinal ?? calc.Rounded,
                row?.TransferredFinal, row?.TransferredAt,
                grade is null ? null : new WorkSheetGradeDto(grade.Id, grade.Value, grade.Status));
        }

        var total = WorkSheetRules.WeightTotal(categories);
        return new WorkSheetDto(sheet?.Id, group.Id, group.Name, group.Status, discipline.Id, discipline.Name,
            period.Id, period.Name, sheet?.TotalLessons, threshold, attendance?.Weight ?? 0, ctx.Mode,
            ctx.IncludeAttendanceInAverage, [.. WorkSheetRules.AverageTerms(categories, ctx.IncludeAttendanceInAverage)],
            categories.Select(c => new WorkSheetCategoryDto(c.Id, c.Name, c.Weight, c.Kind, c.IncludeInAverage,
                c.Kind == AssessmentCategoryKind.Manual && AssessmentCategoryNames.LooksLikeAttendanceName(c.Name),
                c.Works.Select(w => new WorkSheetWorkDto(w.Id, w.Name)).ToList())).ToList(),
            total, WorkSheetRules.HasWeightedCategory(categories) && total == WorkSheetRules.FullWeight,
            WorkSheetRules.TransferReady(categories, ctx),
            rows.Any(r => r.TransferredAt is not null),
            [.. students.Select(s => RowDto(s, true)), .. moved.Select(s => RowDto(s, false))]);
    }

    [HttpPut("settings")]
    public async Task<WorkSheetDto> SaveSettings(Guid groupId, Guid disciplineId, Guid periodId, WorkSheetSettingsRequest r,
        CancellationToken ct)
    {
        var (group, discipline, period) = await LoadForChangeAsync(groupId, disciplineId, periodId, ct);
        var sheet = await FindSheet(groupId, disciplineId, periodId).FirstOrDefaultAsync(ct);
        if (sheet is not null)
        {
            var maxAttended = await Db.WorkSheetRows.Where(x => x.WorkSheetId == sheet.Id && x.AttendedLessons != null)
                .MaxAsync(x => x.AttendedLessons, ct);
            if (maxAttended is int max && (r.TotalLessons is null || r.TotalLessons < max))
                throw AppException.Validation(nameof(r.TotalLessons), FieldCodes.AttendedExceedsTotal);
        }

        var created = sheet is null;
        sheet ??= NewSheet(groupId, disciplineId, periodId);
        if (sheet.TotalLessons == r.TotalLessons && !created) return await Get(groupId, disciplineId, periodId, ct);

        var changes = new List<ChangeLogField>();
        if (created) changes.Add(new("period", null, period.Name));
        if (sheet.TotalLessons != r.TotalLessons) changes.Add(new("totalLessons", sheet.TotalLessons, r.TotalLessons));
        sheet.TotalLessons = r.TotalLessons;
        sheet.UpdatedAt = time.UtcNow;
        if (!created)
        {
            var categories = await CategoriesAsync(disciplineId, ct);
            var ctx = WorkSheetMath.ContextOf(await Db.DisciplineAssessmentSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.DisciplineId == disciplineId, ct));
            var sheetRows = await Db.WorkSheetRows.Include(x => x.Scores).Where(x => x.WorkSheetId == sheet.Id).ToListAsync(ct);
            foreach (var row in sheetRows) WorkSheetMath.Recalculate(row, categories, sheet.TotalLessons, ctx);
        }
        changeLog.Add(ChangeLogEntityType.WorkSheet, sheet.Id, created ? ChangeLogAction.Created : ChangeLogAction.Updated,
            changes, WorkSheetChangeLog.SheetContext(discipline.Name, group.Name, period.Name, await ActorEmailAsync()));
        await Db.SaveChangesAsync(ct);
        return await Get(groupId, disciplineId, periodId, ct);
    }

    /// <summary>
    /// Saves the given rows (current group students only) of this period's sheet. Scores and weights only change the
    /// working calculation; the manual final grade and transferred grades are kept.
    /// </summary>
    [HttpPut("rows")]
    public async Task<WorkSheetDto> SaveRows(Guid groupId, Guid disciplineId, Guid periodId, WorkSheetRowsRequest r,
        CancellationToken ct)
    {
        var (group, discipline, period) = await LoadForChangeAsync(groupId, disciplineId, periodId, ct);
        var categories = await CategoriesAsync(disciplineId, ct);
        var ctx = WorkSheetMath.ContextOf(await Db.DisciplineAssessmentSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DisciplineId == disciplineId, ct));
        var sheet = await FindSheet(groupId, disciplineId, periodId).FirstOrDefaultAsync(ct);
        var students = await Db.Students.Where(s => s.GroupId == groupId).ToDictionaryAsync(s => s.Id, ct);
        ValidateRows(r, students, categories, sheet?.TotalLessons);

        var created = sheet is null;
        sheet ??= NewSheet(groupId, disciplineId, periodId);
        var rows = created
            ? []
            : await Db.WorkSheetRows.Include(x => x.Scores).Where(x => x.WorkSheetId == sheet.Id).ToDictionaryAsync(x => x.StudentId, ct);
        var actorEmail = await ActorEmailAsync();
        var now = time.UtcNow;
        var changedRows = 0;

        foreach (var input in r.Rows)
        {
            var student = students[input.StudentId!.Value];
            var row = rows.GetValueOrDefault(student.Id);
            var isNew = row is null;
            row ??= new WorkSheetRow { Id = Guid.CreateVersion7(), WorkSheetId = sheet.Id, StudentId = student.Id };

            var changes = new List<ChangeLogField>();
            foreach (var work in categories.SelectMany(c => c.Works))
            {
                var score = row.Scores.FirstOrDefault(s => s.ElementId == work.Id);
                var next = input.Scores?.GetValueOrDefault(work.Id);
                var marker = WorkSheetChangeLog.Marker(WorkSheetChangeLog.ScorePrefix + work.Name,
                    score is not null, next is not null, score?.Value != next);
                if (marker is null) continue;
                changes.Add(marker);
                if (next is null)
                {
                    row.Scores.Remove(score!);
                    Db.WorkSheetScores.Remove(score!);
                }
                else if (score is null) row.Scores.Add(new WorkSheetScore { RowId = row.Id, ElementId = work.Id, Value = next.Value });
                else score.Value = next.Value;
            }
            if (row.AttendedLessons != input.AttendedLessons)
            {
                changes.Add(new("attendedLessons", row.AttendedLessons, input.AttendedLessons));
                row.AttendedLessons = input.AttendedLessons;
            }
            var manualMarker = WorkSheetChangeLog.Marker("manualFinal",
                row.ManualFinal is not null, input.ManualFinal is not null, row.ManualFinal != input.ManualFinal);
            if (manualMarker is not null)
            {
                changes.Add(manualMarker);
                row.ManualFinal = input.ManualFinal;
            }
            if (changes.Count == 0) continue;

            WorkSheetMath.Recalculate(row, categories, sheet.TotalLessons, ctx);
            row.UpdatedAt = now;
            if (isNew) Db.WorkSheetRows.Add(row);
            changeLog.Add(ChangeLogEntityType.WorkSheet, sheet.Id, isNew ? ChangeLogAction.Created : ChangeLogAction.Updated,
                changes, WorkSheetChangeLog.RowContext(student.FullName, discipline.Name, group.Name, period.Name, actorEmail));
            changedRows++;
        }

        if (changedRows > 0)
        {
            sheet.UpdatedAt = now;
            await Db.SaveChangesAsync(ct);
        }
        return await Get(groupId, disciplineId, periodId, ct);
    }

    /// <summary>What a transfer would do: ready rows with the change against the gradebook and skipped rows with reasons.</summary>
    [HttpGet("transfer")]
    public async Task<TransferPreviewDto> TransferPreview(Guid groupId, Guid disciplineId, Guid periodId, CancellationToken ct)
    {
        var plan = await PlanAsync(groupId, disciplineId, periodId, ct);
        return plan.Preview;
    }

    /// <summary>
    /// Writes the final grades (× 10) to the gradebook for the sheet's period through the ordinary grade mechanism:
    /// new grades are drafts, changed published grades stay published and notify the student, unchanged grades are not touched.
    /// Only the final grade is transferred — never scores or attendance. Grades of other periods are never affected.
    /// </summary>
    [HttpPost("transfer")]
    public async Task<TransferResultDto> Transfer(Guid groupId, Guid disciplineId, Guid periodId, TransferRequest r,
        CancellationToken ct)
    {
        await LoadForChangeAsync(groupId, disciplineId, periodId, ct);
        var plan = await PlanAsync(groupId, disciplineId, periodId, ct);
        if (!plan.ReadyForTransfer)
            throw AppException.Conflict(plan.Mode == CalculationMode.Average
                ? ErrorCodes.WorkSheetNoElements
                : ErrorCodes.WorkSheetWeightsIncomplete);

        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < r.Entries.Count; i++)
            if (!seen.Add(r.Entries[i].StudentId!.Value)) errors[$"entries[{i}].studentId"] = [FieldCodes.Duplicate];
        if (errors.Count > 0) throw AppException.Validation(errors);

        var items = new List<PlanItem>();
        foreach (var entry in r.Entries)
        {
            var item = plan.Items.GetValueOrDefault(entry.StudentId!.Value);
            if (item is null || item.Dto.Value != entry.Value) throw AppException.Conflict(ErrorCodes.Conflict);
            items.Add(item);
        }
        if (!r.ConfirmChanges && items.Any(i => i.Dto.Change == TransferChange.Changed))
            throw AppException.Conflict(ErrorCodes.GradeChangesNotConfirmed);

        var actorEmail = await ActorEmailAsync();
        var now = time.UtcNow;
        int createdCount = 0, updatedCount = 0, unchangedCount = 0;
        foreach (var item in items)
        {
            switch (item.Dto.Change)
            {
                case TransferChange.New:
                    writer.CreateDraft(item.Student, plan.Discipline, plan.Period, item.Dto.Value, actorEmail,
                        WorkSheetChangeLog.SourceWorkSheet);
                    createdCount++;
                    break;
                case TransferChange.Changed:
                    writer.ChangeValue(item.Grade!, item.Dto.Value, actorEmail, WorkSheetChangeLog.SourceWorkSheet);
                    updatedCount++;
                    break;
                default:
                    unchangedCount++;
                    break;
            }
            item.Row.TransferredFinal = item.Dto.Final;
            item.Row.TransferredAt = now;
        }
        await Db.SaveChangesAsync(ct);
        return new TransferResultDto(createdCount, updatedCount, unchangedCount);
    }

    private sealed record PlanItem(TransferItemDto Dto, Student Student, WorkSheetRow Row, Grade? Grade);

    private sealed record Plan(Discipline Discipline, AcademicPeriod Period, CalculationMode Mode,
        bool ReadyForTransfer, Dictionary<Guid, PlanItem> Items, TransferPreviewDto Preview);

    private async Task<Plan> PlanAsync(Guid groupId, Guid disciplineId, Guid periodId, CancellationToken ct)
    {
        if (!await Db.Groups.AnyAsync(g => g.Id == groupId, ct)) throw AppException.NotFound();
        var discipline = await Db.Disciplines.FirstOrDefaultAsync(d => d.Id == disciplineId, ct) ?? throw AppException.NotFound();
        var period = await Db.Periods.FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppException.NotFound();
        var categories = await CategoriesAsync(disciplineId, ct);
        var ctx = WorkSheetMath.ContextOf(await Db.DisciplineAssessmentSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DisciplineId == disciplineId, ct));
        var ready = WorkSheetRules.TransferReady(categories, ctx);
        var sheet = await FindSheet(groupId, disciplineId, periodId).FirstOrDefaultAsync(ct);
        var students = await Db.Students.Include(s => s.Group).Where(s => s.GroupId == groupId)
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.MiddleName).ToListAsync(ct);
        var rows = sheet is null
            ? []
            : await Db.WorkSheetRows.Include(x => x.Scores).Include(x => x.Student)
                .Where(x => x.WorkSheetId == sheet.Id).ToListAsync(ct);
        var rowsByStudent = rows.ToDictionary(x => x.StudentId);
        var ids = students.Select(s => s.Id).ToList();
        var grades = await Db.Grades
            .Where(g => g.DisciplineId == disciplineId && g.PeriodId == periodId && ids.Contains(g.StudentId))
            .ToDictionaryAsync(g => g.StudentId, ct);

        var items = new Dictionary<Guid, PlanItem>();
        var skipped = new List<TransferSkipDto>();
        foreach (var student in students)
        {
            var row = rowsByStudent.GetValueOrDefault(student.Id);
            var calc = WorkSheetRules.Calculate(categories, WorkSheetMath.ScoresOf(row, categories),
                row?.AttendedLessons, sheet?.TotalLessons, ctx);
            if (calc.State != CalculationState.Calculated || row is null)
            {
                var reason = calc.State switch
                {
                    CalculationState.NoElements => TransferSkipReason.NoElements,
                    CalculationState.WeightsIncomplete => TransferSkipReason.WeightsIncomplete,
                    _ => TransferSkipReason.MissingScores,
                };
                skipped.Add(new TransferSkipDto(student.Id, student.FullName, reason, calc.MissingCount));
                continue;
            }
            var final = row.ManualFinal ?? calc.Rounded!.Value;
            var value = final * WorkSheetRules.GradebookFactor;
            var grade = grades.GetValueOrDefault(student.Id);
            var change = grade is null ? TransferChange.New
                : grade.Value == value ? TransferChange.Unchanged : TransferChange.Changed;
            items[student.Id] = new PlanItem(
                new TransferItemDto(student.Id, student.FullName, final, row.ManualFinal is not null, value, change,
                    grade?.Value, grade?.Status),
                student, row, grade);
        }
        var inGroup = students.Select(s => s.Id).ToHashSet();
        skipped.AddRange(rows.Where(x => !inGroup.Contains(x.StudentId)).Select(x => x.Student!)
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.MiddleName)
            .Select(s => new TransferSkipDto(s.Id, s.FullName, TransferSkipReason.NotInGroup, 0)));

        return new Plan(discipline, period, ctx.Mode, ready, items,
            new TransferPreviewDto(period.Id, period.Name, WorkSheetRules.GradebookFactor,
                items.Values.Select(i => i.Dto).ToList(), skipped));
    }

    private static void ValidateRows(WorkSheetRowsRequest r, Dictionary<Guid, Student> students,
        List<AssessmentCategory> categories, int? totalLessons)
    {
        var errors = new Dictionary<string, string[]>();
        var elementIds = categories.SelectMany(c => c.Works).Select(w => w.Id).ToHashSet();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var row = r.Rows[i];
            var studentId = row.StudentId!.Value;
            if (!students.ContainsKey(studentId)) errors[$"rows[{i}].studentId"] = [FieldCodes.NotFound];
            else if (!seen.Add(studentId)) errors[$"rows[{i}].studentId"] = [FieldCodes.Duplicate];
            foreach (var (elementId, value) in row.Scores ?? [])
            {
                var key = $"rows[{i}].scores.{elementId}";
                if (!elementIds.Contains(elementId)) errors[key] = [FieldCodes.NotFound];
                else if (value is decimal v && (v < 0 || v > WorkSheetRules.MaxScore)) errors[key] = [FieldCodes.Range];
                else if (value is decimal d && !WorkSheetRules.HasAtMostTwoDecimals(d)) errors[key] = [FieldCodes.TooManyDecimals];
            }
            if (row.AttendedLessons is int attended)
            {
                if (totalLessons is null) errors[$"rows[{i}].attendedLessons"] = [FieldCodes.TotalLessonsRequired];
                else if (attended > totalLessons) errors[$"rows[{i}].attendedLessons"] = [FieldCodes.AttendedExceedsTotal];
            }
        }
        if (errors.Count > 0) throw AppException.Validation(errors);
    }

    private IQueryable<WorkSheet> FindSheet(Guid groupId, Guid disciplineId, Guid periodId) =>
        Db.WorkSheets.Where(s => s.GroupId == groupId && s.DisciplineId == disciplineId && s.PeriodId == periodId);

    private async Task<(Group Group, Discipline Discipline, AcademicPeriod Period)> LoadForChangeAsync(
        Guid groupId, Guid disciplineId, Guid periodId, CancellationToken ct)
    {
        var group = await Db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw AppException.NotFound();
        var discipline = await Db.Disciplines.FirstOrDefaultAsync(d => d.Id == disciplineId, ct) ?? throw AppException.NotFound();
        var period = await Db.Periods.FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppException.NotFound();
        if (group.Status == GroupStatus.Archived) throw AppException.Conflict(ErrorCodes.GroupArchived);
        return (group, discipline, period);
    }

    private async Task<List<AssessmentCategory>> CategoriesAsync(Guid disciplineId, CancellationToken ct)
    {
        var list = await Db.AssessmentCategories.AsNoTracking().Include(c => c.Works)
            .Where(c => c.DisciplineId == disciplineId).ToListAsync(ct);
        foreach (var category in list)
            category.Works = category.Works.OrderBy(w => w.Order).ToList();
        return list.OrderBy(c => c.Kind == AssessmentCategoryKind.Attendance ? 1 : 0).ThenBy(c => c.Order).ToList();
    }

    private WorkSheet NewSheet(Guid groupId, Guid disciplineId, Guid periodId)
    {
        var sheet = new WorkSheet
        {
            Id = Guid.CreateVersion7(), GroupId = groupId, DisciplineId = disciplineId, PeriodId = periodId, UpdatedAt = time.UtcNow
        };
        Db.WorkSheets.Add(sheet);
        return sheet;
    }

    private async Task<string?> ActorEmailAsync()
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }
}
