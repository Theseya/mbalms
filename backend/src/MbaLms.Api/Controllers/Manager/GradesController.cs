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

public record GradeUpdateRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [Range(0, 100, ErrorMessage = FieldCodes.Range)] int? Value);

public record BulkPublishRequest([Required(ErrorMessage = FieldCodes.Required)] List<Guid> Ids);

public record GradePublishedPayload(Guid GradeId, string DisciplineName, string PeriodName);

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
    /// Changing a published grade keeps it published and notifies the student again;
    /// changing a draft does not notify.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<GradeDto> Update(Guid id, GradeUpdateRequest r, CancellationToken ct)
    {
        var grade = await LoadForChange(id, ct);
        if (grade.Value != r.Value)
        {
            Record(grade, GradeChangeAction.Updated, grade.Value, grade.Status, newValue: r.Value);
            grade.Value = r.Value!.Value;
            grade.UpdatedAt = time.UtcNow;
            if (grade.Status == GradeStatus.Published)
            {
                grade.PublishedAt = time.UtcNow;
                Notify(grade);
            }
            await Db.SaveChangesAsync(ct);
        }
        return await Get(id, ct);
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
        Notify(grade);
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

    private void Notify(Grade grade) =>
        notifications.Add(grade.Student!.UserId, NotificationType.GradePublished,
            new GradePublishedPayload(grade.Id, grade.Discipline!.Name, grade.Period!.Name));

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
