using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// The single way final grades are created and changed: every change adds its GradeHistory entry, ChangeLog row and
/// (for published grades) the student notification to the same SaveChanges. Does not call SaveChanges itself.
/// </summary>
public class GradeWriter(
    AppDbContext db, AppTime time, NotificationService notifications, CurrentUser currentUser, ChangeLogWriter changeLog)
{
    /// <summary>The student must have its Group loaded.</summary>
    public Grade CreateDraft(Student student, Discipline discipline, AcademicPeriod period, int value, string? actorEmail,
        string? source = null)
    {
        var grade = new Grade
        {
            Id = Guid.CreateVersion7(),
            StudentId = student.Id,
            DisciplineId = discipline.Id,
            PeriodId = period.Id,
            Value = value,
            Status = GradeStatus.Draft,
            UpdatedAt = time.UtcNow,
            Student = student,
            Discipline = discipline,
            Period = period
        };
        db.Grades.Add(grade);
        Record(grade, GradeChangeAction.Created, null, null);
        var snap = GradeChangeLog.From(grade);
        changeLog.Add(
            ChangeLogEntityType.Grade, grade.Id, ChangeLogAction.Created,
            GradeChangeLog.ForCreate(snap), GradeChangeLog.Context(snap, actorEmail, source));
        return grade;
    }

    /// <summary>
    /// Returns false if the value is unchanged (nothing is recorded). A published grade stays published and the
    /// student is notified again. The grade must have Student (with Group), Discipline and Period loaded.
    /// </summary>
    public bool ChangeValue(Grade grade, int value, string? actorEmail, string? source = null)
    {
        if (grade.Value == value) return false;
        Record(grade, GradeChangeAction.Updated, grade.Value, grade.Status, newValue: value);
        var snap = GradeChangeLog.From(grade);
        changeLog.Add(
            ChangeLogEntityType.Grade, grade.Id, ChangeLogAction.Updated,
            GradeChangeLog.ForValueUpdate(), GradeChangeLog.Context(snap, actorEmail, source));
        grade.Value = value;
        grade.UpdatedAt = time.UtcNow;
        if (grade.Status == GradeStatus.Published)
        {
            grade.PublishedAt = time.UtcNow;
            Notify(grade, "updated");
        }
        return true;
    }

    public void Publish(Grade grade, string? actorEmail)
    {
        var oldStatus = grade.Status;
        Record(grade, GradeChangeAction.Published, grade.Value, grade.Status, newStatus: GradeStatus.Published);
        grade.Status = GradeStatus.Published;
        grade.PublishedAt = time.UtcNow;
        grade.UpdatedAt = time.UtcNow;
        var snap = GradeChangeLog.From(grade);
        changeLog.Add(
            ChangeLogEntityType.Grade, grade.Id, ChangeLogAction.Published,
            GradeChangeLog.StatusOnly(oldStatus, grade.Status), GradeChangeLog.Context(snap, actorEmail));
        Notify(grade, "published");
    }

    /// <summary>
    /// Adds a history entry to the same SaveChanges as the change itself. Must be called before the grade is mutated:
    /// new value and status default to the grade's current ones.
    /// </summary>
    public void Record(Grade grade, GradeChangeAction action, int? oldValue, GradeStatus? oldStatus,
        int? newValue = null, GradeStatus? newStatus = null, bool deleted = false) =>
        db.GradeHistory.Add(new GradeHistoryEntry
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
}
