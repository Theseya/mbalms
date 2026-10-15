namespace MbaLms.Api.Domain;

public static class Roles
{
    public const string Manager = "Manager";
    public const string Student = "Student";
}

public enum GroupStatus
{
    Active = 0,
    Archived = 1
}

public enum LessonFormat
{
    Offline = 0,
    Online = 1,
    Hybrid = 2
}

/// <summary>A cancelled lesson is kept and shown to students as cancelled; it is not deleted.</summary>
public enum LessonStatus
{
    Scheduled = 0,
    Cancelled = 1
}

public enum GradeStatus
{
    Draft = 0,
    Published = 1
}

public enum GradeChangeAction
{
    Created = 0,
    Updated = 1,
    Published = 2,
    Unpublished = 3,
    Deleted = 4
}

public enum SurveyType
{
    TeachingEvaluation = 0,
    ServiceSurvey = 1
}

public enum SurveyStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2
}

public enum QuestionType
{
    Scale = 0,
    SingleChoice = 1,
    Text = 2
}

public enum NotificationType
{
    SurveyAssigned = 0,
    ScheduleChanged = 1,
    GradePublished = 2
}

public enum AnnouncementStatus
{
    Draft = 0,
    Published = 1,
    Unpublished = 2
}

/// <summary>Entity kinds recorded in the append-only change log. Extend as domains are wired.</summary>
public enum ChangeLogEntityType
{
    Announcement = 0,
    Lesson = 1,
    Survey = 2,
    SurveyTemplate = 3,
    Grade = 4,
    Group = 5,
    Discipline = 6,
    AcademicPeriod = 7,
    Teacher = 8,
    Student = 9,
    WorkSheet = 10,
    AssessmentElement = 11,
    AssessmentCategory = 12
}

public enum AssessmentCategoryKind
{
    Manual = 0,
    Attendance = 1
}

/// <summary>How the working-sheet total is computed. Stored on the discipline, not on a single sheet.</summary>
public enum CalculationMode
{
    Weighted = 0,
    Average = 1
}

public enum ChangeLogAction
{
    Created = 0,
    Updated = 1,
    Published = 2,
    Unpublished = 3,
    Deleted = 4,
    Opened = 5,
    Closed = 6
}
