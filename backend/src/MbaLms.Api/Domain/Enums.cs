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
