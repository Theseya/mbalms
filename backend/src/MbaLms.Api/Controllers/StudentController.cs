using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers;

public record StudentLessonDto(Guid Id, string DisciplineName, string TeacherName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateTime StartsAtLocal, DateTime EndsAtLocal,
    LessonFormat? Format, string? Location, string? Comment);

public record StudentGradeDto(Guid Id, string DisciplineName, string PeriodName, int Value, DateTimeOffset? PublishedAt);

public record StudentSurveyListItemDto(Guid Id, SurveyType Type, string Title, SurveyStatus Status,
    string? TeacherName, string? DisciplineName, DateTime? OpensAtLocal, DateTime? ClosesAtLocal,
    bool Submitted, DateTimeOffset? SubmittedAt, bool CanRespond);

public record StudentAnswerDto(Guid QuestionId, int? IntValue, Guid? OptionId, string? TextValue);

public record StudentSurveyDetailDto(Guid Id, SurveyType Type, string Title, string? Description, SurveyStatus Status,
    string? TeacherName, string? DisciplineName, DateTime? OpensAtLocal, DateTime? ClosesAtLocal,
    bool Submitted, DateTimeOffset? SubmittedAt, bool CanRespond,
    List<SurveyQuestionDto> Questions, List<StudentAnswerDto> MyAnswers);

public record DashboardDto(StudentLessonDto? NextLesson, List<StudentLessonDto> UpcomingLessons,
    int PendingSurveys, int UnreadNotifications, List<StudentGradeDto> RecentGrades);

public record SubmitAnswerRequest([Required(ErrorMessage = FieldCodes.Required)] Guid? QuestionId,
    int? IntValue, Guid? OptionId, [MaxLength(4000, ErrorMessage = FieldCodes.MaxLength)] string? TextValue);

public record SubmitResponseRequest([Required(ErrorMessage = FieldCodes.Required)] List<SubmitAnswerRequest> Answers);

/// <summary>
/// Student cabinet. Every query is scoped to the signed-in student's own profile and group.
/// </summary>
[ApiController]
[Route("api/student")]
[Authorize(Policy = Roles.Student)]
public class StudentController(AppDbContext db, CurrentUser current, AppTime time) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<DashboardDto> Dashboard(CancellationToken ct)
    {
        var student = await current.GetStudentAsync(ct);
        var now = time.UtcNow;
        var upcoming = await LessonsQuery(student.GroupId).Where(l => l.EndsAt >= now)
            .OrderBy(l => l.StartsAt).Take(5).ToListAsync(ct);
        var lessons = upcoming.Select(ToDto).ToList();

        var surveys = await VisibleSurveys(student).Where(s => s.Status == SurveyStatus.Open)
            .Where(s => !s.Responses.Any(r => r.StudentId == student.Id))
            .Select(s => new { s.OpensAt, s.ClosesAt }).ToListAsync(ct);
        var pending = surveys.Count(s => IsWithinWindow(s.OpensAt, s.ClosesAt, now));

        var unread = await db.Notifications.CountAsync(n => n.UserId == student.UserId && n.ReadAt == null, ct);
        var recent = await ToDto(PublishedGrades(student.Id).OrderByDescending(g => g.PublishedAt).Take(3)).ToListAsync(ct);

        return new DashboardDto(lessons.FirstOrDefault(), lessons, student.Group!.Status == GroupStatus.Active ? pending : 0,
            unread, recent);
    }

    [HttpGet("schedule")]
    public async Task<List<StudentLessonDto>> Schedule([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var student = await current.GetStudentAsync(ct);
        var q = LessonQueries.Filter(LessonsQuery(student.GroupId), time, null, from, to);
        var lessons = await q.OrderBy(l => l.StartsAt).ToListAsync(ct);
        return lessons.Select(ToDto).ToList();
    }

    /// <summary>Only the student's own published grades. Drafts are never returned.</summary>
    [HttpGet("grades")]
    public async Task<List<StudentGradeDto>> Grades(CancellationToken ct)
    {
        var student = await current.GetStudentAsync(ct);
        return await ToDto(PublishedGrades(student.Id).OrderBy(g => g.Period!.Name).ThenBy(g => g.Discipline!.Name))
            .ToListAsync(ct);
    }

    [HttpGet("surveys")]
    public async Task<List<StudentSurveyListItemDto>> Surveys(CancellationToken ct)
    {
        var student = await current.GetStudentAsync(ct);
        var now = time.UtcNow;
        var rows = await VisibleSurveys(student).OrderByDescending(s => s.PublishedAt).Select(s => new
        {
            s.Id, s.Type, s.Title, s.Status, s.Teacher, DisciplineName = s.Discipline != null ? s.Discipline.Name : null,
            s.OpensAt, s.ClosesAt,
            SubmittedAt = s.Responses.Where(r => r.StudentId == student.Id).Select(r => (DateTimeOffset?)r.SubmittedAt).FirstOrDefault()
        }).ToListAsync(ct);

        return rows.Select(s => new StudentSurveyListItemDto(s.Id, s.Type, s.Title, s.Status, s.Teacher?.FullName, s.DisciplineName,
            time.ToLocal(s.OpensAt), time.ToLocal(s.ClosesAt), s.SubmittedAt is not null, s.SubmittedAt,
            CanRespond(student, s.Status, s.OpensAt, s.ClosesAt, s.SubmittedAt is not null, now))).ToList();
    }

    [HttpGet("surveys/{id:guid}")]
    public async Task<StudentSurveyDetailDto> Survey(Guid id, CancellationToken ct)
    {
        var student = await current.GetStudentAsync(ct);
        var s = await VisibleSurveys(student).AsSplitQuery()
                    .Include(x => x.Teacher).Include(x => x.Discipline)
                    .Include(x => x.Questions).ThenInclude(q => q.Options)
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound();

        // Only the student's own response is loaded; other students' answers are never exposed.
        var mine = await db.SurveyResponses.AsNoTracking().Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.SurveyId == id && r.StudentId == student.Id, ct);

        return new StudentSurveyDetailDto(s.Id, s.Type, s.Title, s.Description, s.Status, s.Teacher?.FullName, s.Discipline?.Name,
            time.ToLocal(s.OpensAt), time.ToLocal(s.ClosesAt), mine is not null, mine?.SubmittedAt,
            CanRespond(student, s.Status, s.OpensAt, s.ClosesAt, mine is not null, time.UtcNow),
            SurveysController.MapQuestions(s.Questions),
            mine?.Answers.Select(a => new StudentAnswerDto(a.QuestionId, a.IntValue, a.OptionId, a.TextValue)).ToList() ?? []);
    }

    [HttpPost("surveys/{id:guid}/responses")]
    public async Task<ActionResult<object>> Submit(Guid id, SubmitResponseRequest request, CancellationToken ct)
    {
        var student = await current.GetStudentAsync(ct);
        var survey = await VisibleSurveys(student).Include(s => s.Questions).ThenInclude(q => q.Options)
                         .FirstOrDefaultAsync(s => s.Id == id, ct)
                     ?? throw AppException.NotFound();

        if (await db.SurveyResponses.AnyAsync(r => r.SurveyId == id && r.StudentId == student.Id, ct))
            throw AppException.Conflict(ErrorCodes.SurveyAlreadySubmitted);
        if (!CanRespond(student, survey.Status, survey.OpensAt, survey.ClosesAt, false, time.UtcNow))
            throw AppException.Conflict(ErrorCodes.SurveyNotAccepting);

        var response = new SurveyResponse
        {
            Id = Guid.CreateVersion7(),
            SurveyId = survey.Id,
            StudentId = student.Id,
            SubmittedAt = time.UtcNow,
            Answers = SurveyAnswerValidator.Validate(survey, request.Answers)
        };
        db.SurveyResponses.Add(response);
        await db.SaveChangesAsync(ct);
        return Created($"/api/student/surveys/{id}", new { id = response.Id, submittedAt = response.SubmittedAt });
    }

    private IQueryable<Lesson> LessonsQuery(Guid groupId) =>
        db.Lessons.AsNoTracking().Include(l => l.Discipline).Include(l => l.Teacher).Where(l => l.GroupId == groupId);

    private IQueryable<Survey> VisibleSurveys(Student student) =>
        db.Surveys.AsNoTracking().Where(s => s.GroupId == student.GroupId && s.Status != SurveyStatus.Draft);

    private IQueryable<Grade> PublishedGrades(Guid studentId) =>
        db.Grades.AsNoTracking().Where(g => g.StudentId == studentId && g.Status == GradeStatus.Published);

    private static IQueryable<StudentGradeDto> ToDto(IQueryable<Grade> grades) =>
        grades.Select(g => new StudentGradeDto(g.Id, g.Discipline!.Name, g.Period!.Name, g.Value, g.PublishedAt));

    private StudentLessonDto ToDto(Lesson l) => new(l.Id, l.Discipline!.Name, l.Teacher!.FullName,
        l.StartsAt, l.EndsAt, time.ToLocal(l.StartsAt), time.ToLocal(l.EndsAt), l.Format, l.Location, l.Comment);

    private static bool IsWithinWindow(DateTimeOffset? opens, DateTimeOffset? closes, DateTimeOffset now) =>
        (opens is null || now >= opens) && (closes is null || now < closes);

    private static bool CanRespond(Student student, SurveyStatus status, DateTimeOffset? opens, DateTimeOffset? closes,
        bool submitted, DateTimeOffset now) =>
        !submitted && status == SurveyStatus.Open && student.Group!.Status == GroupStatus.Active
        && IsWithinWindow(opens, closes, now);
}

public static class SurveyAnswerValidator
{
    /// <summary>Checks required questions, value ranges and option ownership; returns answer entities.</summary>
    public static List<SurveyAnswer> Validate(Survey survey, IReadOnlyList<SubmitAnswerRequest> answers)
    {
        var errors = new Dictionary<string, string[]>();
        var byQuestion = new Dictionary<Guid, SubmitAnswerRequest>();
        foreach (var a in answers)
        {
            if (a.QuestionId is null || survey.Questions.All(q => q.Id != a.QuestionId) || !byQuestion.TryAdd(a.QuestionId.Value, a))
            {
                errors["answers"] = [FieldCodes.Invalid];
                throw AppException.Validation(errors);
            }
        }

        var result = new List<SurveyAnswer>();
        foreach (var q in survey.Questions.OrderBy(q => q.Order))
        {
            var key = q.Id.ToString();
            byQuestion.TryGetValue(q.Id, out var a);
            var answer = new SurveyAnswer { Id = Guid.CreateVersion7(), QuestionId = q.Id };
            var provided = false;

            switch (q.Type)
            {
                case QuestionType.Scale when a?.IntValue is not null:
                    if (a.IntValue < q.ScaleMin || a.IntValue > q.ScaleMax) { errors[key] = [FieldCodes.Range]; continue; }
                    answer.IntValue = a.IntValue;
                    provided = true;
                    break;
                case QuestionType.SingleChoice when a?.OptionId is not null:
                    if (q.Options.All(o => o.Id != a.OptionId)) { errors[key] = [FieldCodes.Invalid]; continue; }
                    answer.OptionId = a.OptionId;
                    provided = true;
                    break;
                case QuestionType.Text when !string.IsNullOrWhiteSpace(a?.TextValue):
                    var text = a.TextValue.Trim();
                    if (text.Length > 4000) { errors[key] = [FieldCodes.MaxLength]; continue; }
                    answer.TextValue = text;
                    provided = true;
                    break;
            }

            if (provided) result.Add(answer);
            else if (q.IsRequired) errors[key] = [FieldCodes.Required];
        }

        if (errors.Count > 0) throw AppException.Validation(errors);
        return result;
    }
}
