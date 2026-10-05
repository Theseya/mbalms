using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record LessonDto(Guid Id, Guid GroupId, string GroupName, GroupStatus GroupStatus,
    Guid DisciplineId, string DisciplineName, Guid TeacherId, string TeacherName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTime StartsAtLocal, DateTime EndsAtLocal,
    LessonFormat? Format, string? Location, string? Comment, LessonStatus Status);

/// <param name="StartsAt">Wall-clock time in the application time zone, without offset (e.g. 2026-10-05T10:00).</param>
/// <param name="EndsAt">Wall-clock time in the application time zone, without offset; same date as StartsAt.</param>
/// <param name="Status">Defaults to Scheduled.</param>
public record LessonRequest(
    [Required(ErrorMessage = FieldCodes.Required)] Guid? GroupId,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? DisciplineId,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? TeacherId,
    [Required(ErrorMessage = FieldCodes.Required)] DateTime? StartsAt,
    [Required(ErrorMessage = FieldCodes.Required)] DateTime? EndsAt,
    LessonFormat? Format,
    [MaxLength(500, ErrorMessage = FieldCodes.MaxLength)] string? Location,
    [MaxLength(2000, ErrorMessage = FieldCodes.MaxLength)] string? Comment,
    LessonStatus? Status = null);

/// <summary>Another scheduled lesson of the same group or teacher at an overlapping time.</summary>
public record LessonOverlapDto(Guid Id, string GroupName, string DisciplineName, string TeacherName,
    DateTime StartsAtLocal, DateTime EndsAtLocal, bool SameGroup, bool SameTeacher);

public record ScheduleChangedPayload(Guid LessonId, string Change, string DisciplineName, DateTime StartsAtLocal);

[Route("api/manager/lessons")]
public class LessonsController(AppDbContext db, AppTime time, NotificationService notifications) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<LessonDto>> List([FromQuery] Guid? groupId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var q = LessonQueries.Filter(Db.Lessons.AsNoTracking(), time, groupId, from, to);
        if (groupId is null && !includeArchived) q = q.Where(l => l.Group!.Status == GroupStatus.Active);
        return LessonQueries.ToDtos(await LessonQueries.Load(q, ct), time);
    }

    /// <summary>
    /// Informational check for the lesson form: overlaps are reported, never blocked or resolved automatically.
    /// Times are wall-clock values in the application time zone, like in <see cref="LessonRequest"/>.
    /// </summary>
    [HttpGet("overlaps")]
    public async Task<List<LessonOverlapDto>> Overlaps([FromQuery] DateTime? startsAt, [FromQuery] DateTime? endsAt,
        [FromQuery] Guid? groupId, [FromQuery] Guid? teacherId, [FromQuery] Guid? excludeId, CancellationToken ct)
    {
        if (startsAt is null || endsAt is null || (groupId is null && teacherId is null)) return [];
        var starts = time.ToUtc(startsAt.Value, nameof(startsAt));
        var ends = time.ToUtc(endsAt.Value, nameof(endsAt));
        if (ends <= starts) return [];

        var q = Db.Lessons.AsNoTracking()
            .Where(l => l.Status == LessonStatus.Scheduled && l.StartsAt < ends && l.EndsAt > starts)
            .Where(l => l.GroupId == groupId || l.TeacherId == teacherId);
        if (excludeId is not null) q = q.Where(l => l.Id != excludeId);
        return (await LessonQueries.Load(q, ct))
            .Select(l => new LessonOverlapDto(l.Id, l.Group!.Name, l.Discipline!.Name, l.Teacher!.FullName,
                time.ToLocal(l.StartsAt), time.ToLocal(l.EndsAt), l.GroupId == groupId, l.TeacherId == teacherId))
            .ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<LessonDto> Get(Guid id, CancellationToken ct)
    {
        var lessons = await LessonQueries.Load(Db.Lessons.AsNoTracking().Where(l => l.Id == id), ct);
        return LessonQueries.ToDtos(lessons, time).FirstOrDefault() ?? throw AppException.NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<LessonDto>> Create(LessonRequest r, CancellationToken ct)
    {
        var lesson = new Lesson { Id = Guid.CreateVersion7() };
        await ApplyAsync(lesson, r, ct);
        Db.Lessons.Add(lesson);
        await NotifyAsync(lesson, lesson.GroupId, "created", ct);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = lesson.Id }, await Get(lesson.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<LessonDto> Update(Guid id, LessonRequest r, CancellationToken ct)
    {
        var lesson = await Db.Lessons.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw AppException.NotFound();
        var oldGroupId = lesson.GroupId;
        var wasScheduled = lesson.Status == LessonStatus.Scheduled;
        await RequireActiveGroupAsync(oldGroupId, nameof(r.GroupId), ct);
        await ApplyAsync(lesson, r, ct);
        Db.ChangeTracker.DetectChanges();
        // Saving the form without changes must not spam the group with notifications.
        if (Db.Entry(lesson).Properties.Any(p => p.IsModified))
        {
            var change = wasScheduled && lesson.Status == LessonStatus.Cancelled ? "cancelled" : "updated";
            await NotifyAsync(lesson, lesson.GroupId, change, ct);
            if (oldGroupId != lesson.GroupId) await NotifyAsync(lesson, oldGroupId, "deleted", ct);
        }
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var lesson = await Db.Lessons.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw AppException.NotFound();
        await RequireActiveGroupAsync(lesson.GroupId, "groupId", ct);
        await NotifyAsync(lesson, lesson.GroupId, "deleted", ct);
        Db.Lessons.Remove(lesson);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task ApplyAsync(Lesson lesson, LessonRequest r, CancellationToken ct)
    {
        await RequireActiveGroupAsync(r.GroupId, nameof(r.GroupId), ct);
        await RequireExistsAsync<Discipline>(r.DisciplineId, nameof(r.DisciplineId), ct);
        await RequireExistsAsync<Teacher>(r.TeacherId, nameof(r.TeacherId), ct);
        var starts = time.ToUtc(r.StartsAt!.Value, nameof(r.StartsAt));
        var ends = time.ToUtc(r.EndsAt!.Value, nameof(r.EndsAt));
        if (ends <= starts) throw AppException.Validation(nameof(r.EndsAt), FieldCodes.EndBeforeStart);
        // The form has a single date: a lesson must not cross midnight in the application time zone.
        if (r.EndsAt.Value.Date != r.StartsAt.Value.Date)
            throw AppException.Validation(nameof(r.EndsAt), FieldCodes.NotSameDay);

        lesson.GroupId = r.GroupId!.Value;
        lesson.DisciplineId = r.DisciplineId!.Value;
        lesson.TeacherId = r.TeacherId!.Value;
        lesson.StartsAt = starts;
        lesson.EndsAt = ends;
        lesson.Format = r.Format;
        lesson.Location = Clean(r.Location);
        lesson.Comment = Clean(r.Comment);
        lesson.Status = r.Status ?? LessonStatus.Scheduled;
    }

    private async Task NotifyAsync(Lesson lesson, Guid groupId, string change, CancellationToken ct)
    {
        var discipline = await Db.Disciplines.Where(d => d.Id == lesson.DisciplineId).Select(d => d.Name).FirstAsync(ct);
        await notifications.NotifyGroupAsync(groupId, NotificationType.ScheduleChanged,
            new ScheduleChangedPayload(lesson.Id, change, discipline, time.ToLocal(lesson.StartsAt)), ct);
    }
}

public static class LessonQueries
{
    /// <summary>Dates are calendar days in the application time zone; a lesson matches if it touches the range.</summary>
    public static IQueryable<Lesson> Filter(IQueryable<Lesson> q, AppTime time, Guid? groupId, DateOnly? from, DateOnly? to)
    {
        if (from is not null && to is not null && to < from) throw AppException.Validation("to", FieldCodes.EndBeforeStart);
        if (groupId is not null) q = q.Where(l => l.GroupId == groupId);
        if (from is not null)
        {
            var f = time.ToUtc(from.Value.ToDateTime(TimeOnly.MinValue), "from");
            q = q.Where(l => l.EndsAt >= f);
        }
        if (to is not null)
        {
            var t = time.ToUtc(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), "to");
            q = q.Where(l => l.StartsAt < t);
        }
        return q;
    }

    public static Task<List<Lesson>> Load(IQueryable<Lesson> q, CancellationToken ct) =>
        q.Include(l => l.Group).Include(l => l.Discipline).Include(l => l.Teacher)
            .OrderBy(l => l.StartsAt).ToListAsync(ct);

    public static List<LessonDto> ToDtos(IEnumerable<Lesson> lessons, AppTime time) =>
        lessons.Select(l => new LessonDto(l.Id, l.GroupId, l.Group!.Name, l.Group.Status,
            l.DisciplineId, l.Discipline!.Name, l.TeacherId, l.Teacher!.FullName,
            l.StartsAt, l.EndsAt, time.ToLocal(l.StartsAt), time.ToLocal(l.EndsAt),
            l.Format, l.Location, l.Comment, l.Status)).ToList();
}
