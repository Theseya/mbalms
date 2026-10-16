using Microsoft.AspNetCore.Identity;

namespace MbaLms.Api.Domain;

public class AppUser : IdentityUser<Guid>;

public class MbaProgram
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
}

public class AcademicPeriod
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public class Group
{
    public Guid Id { get; set; }
    public Guid ProgramId { get; set; }
    public MbaProgram? Program { get; set; }
    public required string Name { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public GroupStatus Status { get; set; } = GroupStatus.Active;
    public DateTimeOffset? ArchivedAt { get; set; }
    public List<Student> Students { get; set; } = [];
}

public class Student
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public required string LastName { get; set; }
    public required string FirstName { get; set; }
    public string? MiddleName { get; set; }
    public required string Email { get; set; }
    public Guid GroupId { get; set; }
    public Group? Group { get; set; }

    public string FullName => string.IsNullOrWhiteSpace(MiddleName)
        ? $"{LastName} {FirstName}"
        : $"{LastName} {FirstName} {MiddleName}";
}

public class Teacher
{
    public Guid Id { get; set; }
    public required string LastName { get; set; }
    public required string FirstName { get; set; }
    public string? MiddleName { get; set; }
    public string? Email { get; set; }

    public string FullName => string.IsNullOrWhiteSpace(MiddleName)
        ? $"{LastName} {FirstName}"
        : $"{LastName} {FirstName} {MiddleName}";
}

public class Discipline
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// The single PDF "Программа учебной дисциплины" of a discipline. The file itself lives in document storage
/// under <see cref="StorageKey"/>; the original file name is only displayed, never used as a path.
/// </summary>
public class DisciplineProgram
{
    public Guid DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    public Guid StorageKey { get; set; }
    public required string FileName { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid UploadedByUserId { get; set; }
}

public class Lesson
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Group? Group { get; set; }
    public Guid DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    public Guid TeacherId { get; set; }
    public Teacher? Teacher { get; set; }
    /// <summary>UTC.</summary>
    public DateTimeOffset StartsAt { get; set; }
    /// <summary>UTC.</summary>
    public DateTimeOffset EndsAt { get; set; }
    public LessonFormat? Format { get; set; }
    public string? Location { get; set; }
    public string? Comment { get; set; }
    public LessonStatus Status { get; set; } = LessonStatus.Scheduled;
}

public class Grade
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }
    public Guid DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    public Guid PeriodId { get; set; }
    public AcademicPeriod? Period { get; set; }
    public int Value { get; set; }
    public GradeStatus Status { get; set; } = GradeStatus.Draft;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

/// <summary>
/// Append-only record of a grade change. Grade, student, discipline and period ids are plain columns
/// (no foreign keys) so the history outlives a deleted draft.
/// </summary>
public class GradeHistoryEntry
{
    public Guid Id { get; set; }
    public Guid GradeId { get; set; }
    public Guid StudentId { get; set; }
    public Guid DisciplineId { get; set; }
    public Guid PeriodId { get; set; }
    public GradeChangeAction Action { get; set; }
    public int? OldValue { get; set; }
    public int? NewValue { get; set; }
    public GradeStatus? OldStatus { get; set; }
    public GradeStatus? NewStatus { get; set; }
    public Guid ChangedByUserId { get; set; }
    public AppUser? ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>
/// A weighted group of works (or the built-in attendance category) of a discipline.
/// Weights of all categories, including attendance, must add up to 100 before results are calculated.
/// </summary>
public class AssessmentCategory
{
    public Guid Id { get; set; }
    public Guid DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    public required string Name { get; set; }
    /// <summary>Percent, two decimals. Used in weighted mode; kept unused in average mode for switching back.</summary>
    public decimal Weight { get; set; }
    public AssessmentCategoryKind Kind { get; set; }
    /// <summary>
    /// Average mode only: when false, this category's works are omitted and empty scores do not block.
    /// Ignored in weighted mode. Default true.
    /// </summary>
    public bool IncludeInAverage { get; set; } = true;
    public int Order { get; set; }
    public List<AssessmentElement> Works { get; set; } = [];
}

/// <summary>
/// One work inside a manual category. Scores are 0–10. The category weight is shared equally by its works.
/// </summary>
public class AssessmentElement
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public AssessmentCategory? Category { get; set; }
    public required string Name { get; set; }
    public int Order { get; set; }
}

/// <summary>Per-discipline working-sheet settings that are not tied to a group.</summary>
public class DisciplineAssessmentSettings
{
    public Guid DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    /// <summary>Required attendance, percent 0–100; null until the manager sets it.</summary>
    public decimal? AttendanceThreshold { get; set; }
    /// <summary>Weighted is the default for existing disciplines. Applies to every group and period of the discipline.</summary>
    public CalculationMode CalculationMode { get; set; }
    /// <summary>Average mode only: automatic attendance enters the mean as one extra score.</summary>
    public bool IncludeAttendanceInAverage { get; set; }
}

/// <summary>
/// The manager's working sheet (рабочая ведомость) of one group, discipline and academic period. Intermediate
/// scores and attendance stay here; only the final grade is transferred to <see cref="Grade"/> of <see cref="PeriodId"/>.
/// Each period has its own sheet; the period of a sheet never changes.
/// </summary>
public class WorkSheet
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Group? Group { get; set; }
    public Guid DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    public Guid PeriodId { get; set; }
    public AcademicPeriod? Period { get; set; }
    public int? TotalLessons { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WorkSheetRow> Rows { get; set; } = [];
}

/// <summary>
/// One student's results in a working sheet. The row stays with the sheet (and its group) when the student moves
/// to another group, so accumulated results are never removed implicitly.
/// </summary>
public class WorkSheetRow
{
    public Guid Id { get; set; }
    public Guid WorkSheetId { get; set; }
    public WorkSheet? WorkSheet { get; set; }
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }
    public int? AttendedLessons { get; set; }
    /// <summary>Weighted sum before rounding; null while any element is empty or the weights are not 100%.</summary>
    public decimal? CalculatedRaw { get; set; }
    /// <summary><see cref="CalculatedRaw"/> rounded by <see cref="FinalGradeRounding"/>.</summary>
    public int? CalculatedRounded { get; set; }
    /// <summary>Manager's final grade 0–10 that overrides the calculated one.</summary>
    public int? ManualFinal { get; set; }
    /// <summary>Final grade (0–10) of the last transfer to the final gradebook.</summary>
    public int? TransferredFinal { get; set; }
    public DateTimeOffset? TransferredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WorkSheetScore> Scores { get; set; } = [];
}

/// <summary>A filled-in score. A missing row means "not filled in", which is different from an explicit 0.</summary>
public class WorkSheetScore
{
    public Guid RowId { get; set; }
    public WorkSheetRow? Row { get; set; }
    public Guid ElementId { get; set; }
    public AssessmentElement? Element { get; set; }
    /// <summary>0–10, two decimals.</summary>
    public decimal Value { get; set; }
}

public class Survey
{
    public Guid Id { get; set; }
    public SurveyType Type { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;
    public Guid GroupId { get; set; }
    public Group? Group { get; set; }
    public Guid? TeacherId { get; set; }
    public Teacher? Teacher { get; set; }
    public Guid? DisciplineId { get; set; }
    public Discipline? Discipline { get; set; }
    /// <summary>UTC; responses are accepted only after this moment if set.</summary>
    public DateTimeOffset? OpensAt { get; set; }
    /// <summary>UTC; responses are accepted only before this moment if set.</summary>
    public DateTimeOffset? ClosesAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>First time the survey was opened; notifications are sent only once.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
    public List<SurveyQuestion> Questions { get; set; } = [];
    public List<SurveyResponse> Responses { get; set; } = [];
}

/// <summary>Reusable survey structure (no group, window, or responses). Independent of launch surveys.</summary>
public class SurveyTemplate
{
    public Guid Id { get; set; }
    public SurveyType Type { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<SurveyTemplateQuestion> Questions { get; set; } = [];
}

public class SurveyTemplateQuestion
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public SurveyTemplate? Template { get; set; }
    public int Order { get; set; }
    public required string Text { get; set; }
    public QuestionType Type { get; set; }
    public bool IsRequired { get; set; }
    public int? ScaleMin { get; set; }
    public int? ScaleMax { get; set; }
    public string? ScaleMinLabel { get; set; }
    public string? ScaleMaxLabel { get; set; }
    public List<SurveyTemplateOption> Options { get; set; } = [];
}

public class SurveyTemplateOption
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public SurveyTemplateQuestion? Question { get; set; }
    public int Order { get; set; }
    public required string Text { get; set; }
}

public class SurveyQuestion
{
    public Guid Id { get; set; }
    public Guid SurveyId { get; set; }
    public Survey? Survey { get; set; }
    public int Order { get; set; }
    public required string Text { get; set; }
    public QuestionType Type { get; set; }
    public bool IsRequired { get; set; }
    public int? ScaleMin { get; set; }
    public int? ScaleMax { get; set; }
    public string? ScaleMinLabel { get; set; }
    public string? ScaleMaxLabel { get; set; }
    public List<SurveyQuestionOption> Options { get; set; } = [];
}

public class SurveyQuestionOption
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public SurveyQuestion? Question { get; set; }
    public int Order { get; set; }
    public required string Text { get; set; }
}

public class SurveyResponse
{
    public Guid Id { get; set; }
    public Guid SurveyId { get; set; }
    public Survey? Survey { get; set; }
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public List<SurveyAnswer> Answers { get; set; } = [];
}

public class SurveyAnswer
{
    public Guid Id { get; set; }
    public Guid ResponseId { get; set; }
    public SurveyResponse? Response { get; set; }
    public Guid QuestionId { get; set; }
    public SurveyQuestion? Question { get; set; }
    public int? IntValue { get; set; }
    public Guid? OptionId { get; set; }
    public SurveyQuestionOption? Option { get; set; }
    public string? TextValue { get; set; }
}

public class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    /// <summary>JSON object with parameters for the localized message on the client.</summary>
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public class Announcement
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    /// <summary>Plain text; line breaks are preserved on display.</summary>
    public required string Body { get; set; }
    public AnnouncementStatus Status { get; set; } = AnnouncementStatus.Draft;
    public bool IsPinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>UTC; set on each Draft/Unpublished → Published transition.</summary>
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>UTC; optional end of student visibility. Expiry does not change Status.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public AppUser? CreatedBy { get; set; }
    public List<AnnouncementGroup> Groups { get; set; } = [];
}

public class AnnouncementGroup
{
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    public Guid GroupId { get; set; }
    public Group? Group { get; set; }
}

public enum AdminAuditAction
{
    OwnerBootstrapped = 0,
    ManagerInvited = 1,
    ManagerBlocked = 2,
    ManagerUnblocked = 3,
    OwnerBlocked = 4,
    OwnerUnblocked = 5,
    AccountAdminBlocked = 6,
    AccountAdminUnblocked = 7,
    OwnerRoleRevoked = 8,
    StaffDeleted = 9
}

/// <summary>
/// Append-only staff-administration audit. Never stores passwords, tokens, or academic payloads.
/// </summary>
public class AdminAuditEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? TargetEmail { get; set; }
    public AdminAuditAction Action { get; set; }
    public required string TargetKind { get; set; }
    public string DetailsJson { get; set; } = "{}";
}

/// <summary>
/// Append-only audit row. EntityId has no FK so history survives hard deletes.
/// ActorUserId is null only for true system actions; deleted users keep their id and a denormalized email in ContextJson.
/// </summary>
public class ChangeLogEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public ChangeLogAction Action { get; set; }
    public ChangeLogEntityType EntityType { get; set; }
    public Guid EntityId { get; set; }
    /// <summary>jsonb array of { field, old, new } for an explicit allow-list of fields.</summary>
    public string ChangesJson { get; set; } = "[]";
    /// <summary>jsonb snapshot: title, groupNames, actorEmail — enough to read after delete/rename.</summary>
    public string ContextJson { get; set; } = "{}";
}
