using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record GradeDto(Guid Id, Guid StudentId, string StudentName, Guid GroupId, string GroupName,
    Guid DisciplineId, string DisciplineName, Guid PeriodId, string PeriodName,
    int Value, GradeStatus Status, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public record GradeCreateRequest(
    [Required(ErrorMessage = FieldCodes.Required)] Guid? StudentId,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? DisciplineId,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? PeriodId,
    [Required(ErrorMessage = FieldCodes.Required)] [Range(0, 100, ErrorMessage = FieldCodes.Range)] int? Value);

/// <param name="ConfirmPublishedChange">Must be true to change the value of a published grade.</param>
public record GradeUpdateRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [Range(0, 100, ErrorMessage = FieldCodes.Range)] int? Value,
    bool ConfirmPublishedChange = false);

public record GradeSheetRowDto(Guid StudentId, string StudentName, Guid? GradeId, int? Value, GradeStatus? Status,
    DateTimeOffset? PublishedAt);

/// <summary>All current students of a group with their grade (if any) for one discipline and period.</summary>
public record GradeSheetDto(Guid GroupId, string GroupName, GroupStatus GroupStatus, Guid DisciplineId, string DisciplineName,
    Guid PeriodId, string PeriodName, List<GradeSheetRowDto> Rows);

/// <param name="Value">Null leaves the student's grade as it is.</param>
public record GradeSheetEntry(
    [Required(ErrorMessage = FieldCodes.Required)] Guid? StudentId,
    [Range(0, 100, ErrorMessage = FieldCodes.Range)] int? Value);

/// <param name="ConfirmPublishedChanges">Must be true if any entry changes a published grade.</param>
public record GradeSheetSaveRequest(
    [Required(ErrorMessage = FieldCodes.Required)] List<GradeSheetEntry> Entries,
    bool ConfirmPublishedChanges = false);

public record BulkPublishRequest([Required(ErrorMessage = FieldCodes.Required)] List<Guid> Ids);

/// <param name="Change">"published" for a (re)publication, "updated" when a published value changes.</param>
public record GradePublishedPayload(Guid GradeId, string DisciplineName, string PeriodName, string Change);

public record GradeHistoryDto(Guid Id, Guid GradeId, GradeChangeAction Action, int? OldValue, int? NewValue,
    GradeStatus? OldStatus, GradeStatus? NewStatus, string? ChangedBy, DateTimeOffset ChangedAt);

[Route("api/manager/grades")]
public class GradesController(AppDbContext db, AppTime time, NotificationService notifications, CurrentUser currentUser)
    : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<GradeDto>> List([FromQuery] Guid? groupId, [FromQuery] Guid? periodId, [FromQuery] Guid? disciplineId,
        [FromQuery] Guid? studentId, [FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var q = Query();
        if (groupId is not null) q = q.Where(g => g.Student!.GroupId == groupId);
        else if (!includeArchived) q = q.Where(g => g.Student!.Group!.Status == GroupStatus.Active);
        if (periodId is not null) q = q.Where(g => g.PeriodId == periodId);
        if (disciplineId is not null) q = q.Where(g => g.DisciplineId == disciplineId);
        if (studentId is not null) q = q.Where(g => g.StudentId == studentId);
        var list = await q.OrderBy(g => g.Student!.LastName).ThenBy(g => g.Student!.FirstName)
            .ThenBy(g => g.Discipline!.Name).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<GradeDto> Get(Guid id, CancellationToken ct) =>
        ToDto(await Query().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw AppException.NotFound());

    /// <summary>
    /// History for the grade's student, discipline and period, newest first. Includes entries of
    /// previously deleted drafts for the same combination. Available for archived groups too.
    /// </summary>
    [HttpGet("{id:guid}/history")]
    public async Task<List<GradeHistoryDto>> History(Guid id, CancellationToken ct)
    {
        var grade = await Db.Grades.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw AppException.NotFound();
        return await Db.GradeHistory.AsNoTracking()
            .Where(h => h.StudentId == grade.StudentId && h.DisciplineId == grade.DisciplineId && h.PeriodId == grade.PeriodId)
            .OrderByDescending(h => h.ChangedAt).ThenByDescending(h => h.Id)
            .Select(h => new GradeHistoryDto(h.Id, h.GradeId, h.Action, h.OldValue, h.NewValue,
                h.OldStatus, h.NewStatus, h.ChangedBy!.Email, h.ChangedAt))
            .ToListAsync(ct);
    }

    [HttpPost]
    public async Task<ActionResult<GradeDto>> Create(GradeCreateRequest r, CancellationToken ct)
    {
        var student = await Db.Students.Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == r.StudentId, ct)
                      ?? throw AppException.Validation(nameof(r.StudentId), FieldCodes.NotFound);
        if (student.Group!.Status == GroupStatus.Archived) throw AppException.Conflict(ErrorCodes.GroupArchived);
        await RequireExistsAsync<Discipline>(r.DisciplineId, nameof(r.DisciplineId), ct);
        await RequireExistsAsync<AcademicPeriod>(r.PeriodId, nameof(r.PeriodId), ct);
        if (await Db.Grades.AnyAsync(g => g.StudentId == r.StudentId && g.DisciplineId == r.DisciplineId && g.PeriodId == r.PeriodId, ct))
            throw AppException.Conflict(ErrorCodes.Duplicate);

        var grade = new Grade
        {
            Id = Guid.CreateVersion7(),
            StudentId = student.Id,
            DisciplineId = r.DisciplineId!.Value,
            PeriodId = r.PeriodId!.Value,
            Value = r.Value!.Value,
            Status = GradeStatus.Draft,
            UpdatedAt = time.UtcNow
        };
        Db.Grades.Add(grade);
        Record(grade, GradeChangeAction.Created, null, null);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = grade.Id }, await Get(grade.Id, ct));
    }

    /// <summary>
    /// Changing a published grade requires explicit confirmation, keeps it published and notifies the student again;
    /// changing a draft does not notify.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<GradeDto> Update(Guid id, GradeUpdateRequest r, CancellationToken ct)
    {
        var grade = await LoadForChange(id, ct);
        if (grade.Value != r.Value && grade.Status == GradeStatus.Published && !r.ConfirmPublishedChange)
            throw AppException.Conflict(ErrorCodes.PublishedChangeNotConfirmed);
        if (ChangeValue(grade, r.Value!.Value)) await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpGet("sheet/{groupId:guid}/{disciplineId:guid}/{periodId:guid}")]
    public async Task<GradeSheetDto> Sheet(Guid groupId, Guid disciplineId, Guid periodId, CancellationToken ct)
    {
        var group = await Db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw AppException.NotFound();
        var discipline = await Db.Disciplines.AsNoTracking().FirstOrDefaultAsync(d => d.Id == disciplineId, ct) ?? throw AppException.NotFound();
        var period = await Db.Periods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppException.NotFound();

        var students = await Db.Students.AsNoTracking().Where(s => s.GroupId == groupId)
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).ThenBy(s => s.Id).ToListAsync(ct);
        var grades = await Db.Grades.AsNoTracking()
            .Where(g => g.Student!.GroupId == groupId && g.DisciplineId == disciplineId && g.PeriodId == periodId)
            .ToDictionaryAsync(g => g.StudentId, ct);

        var rows = students.Select(s => grades.TryGetValue(s.Id, out var g)
            ? new GradeSheetRowDto(s.Id, s.FullName, g.Id, g.Value, g.Status, g.PublishedAt)
            : new GradeSheetRowDto(s.Id, s.FullName, null, null, null, null)).ToList();
        return new GradeSheetDto(group.Id, group.Name, group.Status, discipline.Id, discipline.Name, period.Id, period.Name, rows);
    }

    /// <summary>
    /// Saves the grade sheet in one transaction: new values become drafts, changed values update existing grades.
    /// Students must belong to the group; publishing is a separate step.
    /// </summary>
    [HttpPut("sheet/{groupId:guid}/{disciplineId:guid}/{periodId:guid}")]
    public async Task<GradeSheetDto> SaveSheet(Guid groupId, Guid disciplineId, Guid periodId, GradeSheetSaveRequest r,
        CancellationToken ct)
    {
        var group = await Db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw AppException.NotFound();
        if (group.Status == GroupStatus.Archived) throw AppException.Conflict(ErrorCodes.GroupArchived);
        var discipline = await Db.Disciplines.FirstOrDefaultAsync(d => d.Id == disciplineId, ct) ?? throw AppException.NotFound();
        var period = await Db.Periods.FirstOrDefaultAsync(p => p.Id == periodId, ct) ?? throw AppException.NotFound();

        var students = await Db.Students.Where(s => s.GroupId == groupId).ToDictionaryAsync(s => s.Id, ct);
        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < r.Entries.Count; i++)
        {
            var studentId = r.Entries[i].StudentId!.Value;
            if (!students.ContainsKey(studentId)) errors[$"entries[{i}].studentId"] = [FieldCodes.NotFound];
            else if (!seen.Add(studentId)) errors[$"entries[{i}].studentId"] = [ErrorCodes.Duplicate];
        }
        if (errors.Count > 0) throw AppException.Validation(errors);

        var existing = await Db.Grades.Where(g => g.Student!.GroupId == groupId && g.DisciplineId == disciplineId && g.PeriodId == periodId)
            .ToDictionaryAsync(g => g.StudentId, ct);
        var entries = r.Entries.Where(e => e.Value is not null).ToList();
        if (!r.ConfirmPublishedChanges && entries.Any(e => existing.TryGetValue(e.StudentId!.Value, out var g)
                                                           && g.Status == GradeStatus.Published && g.Value != e.Value))
            throw AppException.Conflict(ErrorCodes.PublishedChangeNotConfirmed);

        foreach (var entry in entries)
        {
            var student = students[entry.StudentId!.Value];
            if (existing.TryGetValue(student.Id, out var grade))
            {
                grade.Student = student;
                grade.Discipline = discipline;
                grade.Period = period;
                ChangeValue(grade, entry.Value!.Value);
                continue;
            }
            grade = new Grade
            {
                Id = Guid.CreateVersion7(),
                StudentId = student.Id,
                DisciplineId = discipline.Id,
                PeriodId = period.Id,
                Value = entry.Value!.Value,
                Status = GradeStatus.Draft,
                UpdatedAt = time.UtcNow
            };
            Db.Grades.Add(grade);
            Record(grade, GradeChangeAction.Created, null, null);
        }
        await Db.SaveChangesAsync(ct);
        return await Sheet(groupId, disciplineId, periodId, ct);
    }

    /// <summary>Returns false if the value is unchanged. The grade must have Student, Discipline and Period loaded.</summary>
    private bool ChangeValue(Grade grade, int value)
    {
        if (grade.Value == value) return false;
        Record(grade, GradeChangeAction.Updated, grade.Value, grade.Status, newValue: value);
        grade.Value = value;
        grade.UpdatedAt = time.UtcNow;
        if (grade.Status == GradeStatus.Published)
        {
            grade.PublishedAt = time.UtcNow;
            Notify(grade, "updated");
        }
        return true;
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<GradeDto> Publish(Guid id, CancellationToken ct)
    {
        var grade = await LoadForChange(id, ct);
        if (grade.Status == GradeStatus.Published) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        PublishGrade(grade);
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Publishes all listed draft grades atomically.</summary>
    [HttpPost("publish")]
    public async Task<ActionResult<object>> PublishMany(BulkPublishRequest r, CancellationToken ct)
    {
        var ids = r.Ids.Distinct().ToList();
        var grades = await Db.Grades.Include(g => g.Discipline).Include(g => g.Period)
            .Include(g => g.Student).ThenInclude(s => s!.Group)
            .Where(g => ids.Contains(g.Id) && g.Status == GradeStatus.Draft).ToListAsync(ct);
        if (grades.Any(g => g.Student!.Group!.Status == GroupStatus.Archived))
            throw AppException.Conflict(ErrorCodes.GroupArchived);
        foreach (var grade in grades) PublishGrade(grade);
        await Db.SaveChangesAsync(ct);
        return new { published = grades.Count };
    }

    [HttpPost("{id:guid}/unpublish")]
    public async Task<GradeDto> Unpublish(Guid id, CancellationToken ct)
    {
        var grade = await LoadForChange(id, ct);
        if (grade.Status == GradeStatus.Draft) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        Record(grade, GradeChangeAction.Unpublished, grade.Value, grade.Status, newStatus: GradeStatus.Draft);
        grade.Status = GradeStatus.Draft;
        grade.PublishedAt = null;
        grade.UpdatedAt = time.UtcNow;
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var grade = await LoadForChange(id, ct);
        if (grade.Status == GradeStatus.Published) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        Record(grade, GradeChangeAction.Deleted, grade.Value, grade.Status, deleted: true);
        Db.Grades.Remove(grade);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private void PublishGrade(Grade grade)
    {
        Record(grade, GradeChangeAction.Published, grade.Value, grade.Status, newStatus: GradeStatus.Published);
        grade.Status = GradeStatus.Published;
        grade.PublishedAt = time.UtcNow;
        grade.UpdatedAt = time.UtcNow;
        Notify(grade, "published");
    }

    /// <summary>
    /// Adds a history entry to the same SaveChanges as the change itself. Must be called before the grade is mutated:
    /// new value and status default to the grade's current ones.
    /// </summary>
    private void Record(Grade grade, GradeChangeAction action, int? oldValue, GradeStatus? oldStatus,
        int? newValue = null, GradeStatus? newStatus = null, bool deleted = false) =>
        Db.GradeHistory.Add(new GradeHistoryEntry
        {
            Id = Guid.CreateVersion7(),
            GradeId = grade.Id,
            StudentId = grade.StudentId,
            DisciplineId = grade.DisciplineId,
            PeriodId = grade.PeriodId,
            Action = action,
            OldValue = oldValue,
            NewValue = deleted ? null : newValue ?? grade.Value,
            OldStatus = oldStatus,
            NewStatus = deleted ? null : newStatus ?? grade.Status,
            ChangedByUserId = currentUser.UserId,
            ChangedAt = time.UtcNow
        });

    private void Notify(Grade grade, string change) =>
        notifications.Add(grade.Student!.UserId, NotificationType.GradePublished,
            new GradePublishedPayload(grade.Id, grade.Discipline!.Name, grade.Period!.Name, change));

    private async Task<Grade> LoadForChange(Guid id, CancellationToken ct)
    {
        var grade = await Db.Grades.Include(g => g.Discipline).Include(g => g.Period)
                        .Include(g => g.Student).ThenInclude(s => s!.Group)
                        .FirstOrDefaultAsync(g => g.Id == id, ct)
                    ?? throw AppException.NotFound();
        if (grade.Student!.Group!.Status == GroupStatus.Archived) throw AppException.Conflict(ErrorCodes.GroupArchived);
        return grade;
    }

    private IQueryable<Grade> Query() =>
        Db.Grades.AsNoTracking().Include(g => g.Student).ThenInclude(s => s!.Group)
            .Include(g => g.Discipline).Include(g => g.Period);

    private static GradeDto ToDto(Grade g) => new(g.Id, g.StudentId, g.Student!.FullName, g.Student.GroupId, g.Student.Group!.Name,
        g.DisciplineId, g.Discipline!.Name, g.PeriodId, g.Period!.Name, g.Value, g.Status, g.UpdatedAt, g.PublishedAt);
}
