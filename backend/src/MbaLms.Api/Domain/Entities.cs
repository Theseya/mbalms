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
