using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record SurveyOptionDto(Guid Id, int Order, string Text);

public record SurveyQuestionDto(Guid Id, int Order, string Text, QuestionType Type, bool IsRequired,
    int? ScaleMin, int? ScaleMax, string? ScaleMinLabel, string? ScaleMaxLabel, List<SurveyOptionDto> Options);

public record SurveyListItemDto(Guid Id, SurveyType Type, string Title, SurveyStatus Status,
    Guid GroupId, string GroupName, GroupStatus GroupStatus, string? TeacherName, string? DisciplineName,
    DateTime? OpensAtLocal, DateTime? ClosesAtLocal, int QuestionCount, int ResponseCount, int StudentCount,
    DateTimeOffset CreatedAt);

public record SurveyDetailDto(Guid Id, SurveyType Type, string Title, string? Description, SurveyStatus Status,
    Guid GroupId, string GroupName, GroupStatus GroupStatus, Guid? TeacherId, string? TeacherName,
    Guid? DisciplineId, string? DisciplineName, DateTime? OpensAtLocal, DateTime? ClosesAtLocal,
    DateTimeOffset? PublishedAt, int ResponseCount, int StudentCount, List<SurveyQuestionDto> Questions);

public record SurveyQuestionRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(1000, ErrorMessage = FieldCodes.MaxLength)] string Text,
    [Required(ErrorMessage = FieldCodes.Required)] QuestionType? Type,
    bool IsRequired,
    int? ScaleMin,
    int? ScaleMax,
    List<string>? Options,
    [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string? ScaleMinLabel = null,
    [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string? ScaleMaxLabel = null);

/// <param name="OpensAt">Optional wall-clock time in the application time zone.</param>
/// <param name="ClosesAt">Optional wall-clock time in the application time zone.</param>
public record SurveyRequest(
    [Required(ErrorMessage = FieldCodes.Required)] SurveyType? Type,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(300, ErrorMessage = FieldCodes.MaxLength)] string Title,
    [MaxLength(4000, ErrorMessage = FieldCodes.MaxLength)] string? Description,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? GroupId,
    Guid? TeacherId,
    Guid? DisciplineId,
    DateTime? OpensAt,
    DateTime? ClosesAt,
    [MaxLength(50, ErrorMessage = FieldCodes.MaxLength)] List<SurveyQuestionRequest>? Questions);

public record ResponseAnswerDto(Guid QuestionId, int? IntValue, Guid? OptionId, string? OptionText, string? TextValue);

public record SurveyResponseDto(Guid Id, Guid StudentId, string StudentName, string GroupName, DateTimeOffset SubmittedAt,
    List<ResponseAnswerDto> Answers);

public record SurveyAssignedPayload(Guid SurveyId, string Title, SurveyType SurveyType);

[Route("api/manager/surveys")]
public class SurveysController(AppDbContext db, AppTime time, NotificationService notifications) : ManagerControllerBase(db)
{
    public const int ScaleLimit = SurveyQuestionBuilder.ScaleLimit;
    public const int MaxOptions = SurveyQuestionBuilder.MaxOptions;

    [HttpGet]
    public async Task<List<SurveyListItemDto>> List([FromQuery] Guid? groupId, [FromQuery] SurveyType? type,
        [FromQuery] SurveyStatus? status, [FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var q = Db.Surveys.AsNoTracking();
        if (groupId is not null) q = q.Where(s => s.GroupId == groupId);
        else if (!includeArchived) q = q.Where(s => s.Group!.Status == GroupStatus.Active);
        if (type is not null) q = q.Where(s => s.Type == type);
        if (status is not null) q = q.Where(s => s.Status == status);

        var rows = await q.OrderByDescending(s => s.CreatedAt).Select(s => new
        {
            s.Id, s.Type, s.Title, s.Status, s.GroupId, GroupName = s.Group!.Name, GroupStatus = s.Group.Status,
            Teacher = s.Teacher, DisciplineName = s.Discipline != null ? s.Discipline.Name : null,
            s.OpensAt, s.ClosesAt, QuestionCount = s.Questions.Count, ResponseCount = s.Responses.Count,
            StudentCount = s.Group.Students.Count, s.CreatedAt
        }).ToListAsync(ct);

        return rows.Select(s => new SurveyListItemDto(s.Id, s.Type, s.Title, s.Status, s.GroupId, s.GroupName, s.GroupStatus,
            s.Teacher?.FullName, s.DisciplineName, time.ToLocal(s.OpensAt), time.ToLocal(s.ClosesAt),
            s.QuestionCount, s.ResponseCount, s.StudentCount, s.CreatedAt)).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<SurveyDetailDto> Get(Guid id, CancellationToken ct)
    {
        var s = await Db.Surveys.AsNoTracking().AsSplitQuery()
                    .Include(x => x.Group).Include(x => x.Teacher).Include(x => x.Discipline)
                    .Include(x => x.Questions).ThenInclude(q => q.Options)
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound();
        var responseCount = await Db.SurveyResponses.CountAsync(r => r.SurveyId == id, ct);
        var studentCount = await Db.Students.CountAsync(st => st.GroupId == s.GroupId, ct);
        return new SurveyDetailDto(s.Id, s.Type, s.Title, s.Description, s.Status, s.GroupId, s.Group!.Name, s.Group.Status,
            s.TeacherId, s.Teacher?.FullName, s.DisciplineId, s.Discipline?.Name,
            time.ToLocal(s.OpensAt), time.ToLocal(s.ClosesAt), s.PublishedAt, responseCount, studentCount,
            MapQuestions(s.Questions));
    }

    [HttpPost]
    public async Task<ActionResult<SurveyDetailDto>> Create(SurveyRequest r, CancellationToken ct)
    {
        var survey = new Survey { Id = Guid.CreateVersion7(), Title = r.Title, CreatedAt = time.UtcNow };
        await ApplyAsync(survey, r, ct);
        Db.Surveys.Add(survey);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = survey.Id }, await Get(survey.Id, ct));
    }

    /// <summary>
    /// Creates a Draft survey by copying a template's type/title/description/questions (new IDs).
    /// Launch fields (group, teacher/discipline, window) come from the request — never from the template.
    /// </summary>
    [HttpPost("from-template")]
    public async Task<ActionResult<SurveyDetailDto>> CreateFromTemplate(CreateSurveyFromTemplateRequest r, CancellationToken ct)
    {
        var template = await Db.SurveyTemplates.AsNoTracking().AsSplitQuery()
                           .Include(t => t.Questions).ThenInclude(q => q.Options)
                           .FirstOrDefaultAsync(t => t.Id == r.TemplateId, ct)
                       ?? throw AppException.NotFound();

        var questions = template.Questions.OrderBy(q => q.Order)
            .Select(SurveyQuestionBuilder.ToRequest).ToList();
        var request = new SurveyRequest(
            template.Type,
            string.IsNullOrWhiteSpace(r.Title) ? template.Title : r.Title.Trim(),
            r.Description is null ? template.Description : Clean(r.Description),
            r.GroupId,
            r.TeacherId,
            r.DisciplineId,
            r.OpensAt,
            r.ClosesAt,
            questions);

        var survey = new Survey { Id = Guid.CreateVersion7(), Title = request.Title, CreatedAt = time.UtcNow };
        await ApplyAsync(survey, request, ct);
        Db.Surveys.Add(survey);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = survey.Id }, await Get(survey.Id, ct));
    }

    /// <summary>
    /// Copies type, title, description, and question structure into a new template.
    /// Does not read or copy responses; does not change the source survey.
    /// </summary>
    [HttpPost("{id:guid}/save-as-template")]
    public async Task<ActionResult<SurveyTemplateDetailDto>> SaveAsTemplate(Guid id, CancellationToken ct)
    {
        var survey = await Db.Surveys.AsNoTracking().AsSplitQuery()
                         .Include(s => s.Questions).ThenInclude(q => q.Options)
                         .FirstOrDefaultAsync(s => s.Id == id, ct)
                     ?? throw AppException.NotFound();

        var now = time.UtcNow;
        var template = new SurveyTemplate
        {
            Id = Guid.CreateVersion7(),
            Type = survey.Type,
            Title = survey.Title,
            Description = survey.Description,
            CreatedAt = now,
            UpdatedAt = now,
            Questions = survey.Questions.OrderBy(q => q.Order)
                .Select((q, i) => SurveyQuestionBuilder.BuildTemplateQuestion(SurveyQuestionBuilder.ToRequest(q), i, $"questions[{i}]"))
                .ToList()
        };
        Db.SurveyTemplates.Add(template);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(SurveyTemplatesController.Get), "SurveyTemplates", new { id = template.Id },
            await SurveyTemplatesController.LoadDetail(Db, template.Id, ct));
    }

    /// <summary>Full edit (including questions) is allowed only while the survey is a draft.</summary>
    [HttpPut("{id:guid}")]
    public async Task<SurveyDetailDto> Update(Guid id, SurveyRequest r, CancellationToken ct)
    {
        var survey = await Db.Surveys.Include(s => s.Questions).ThenInclude(q => q.Options)
                         .FirstOrDefaultAsync(s => s.Id == id, ct)
                     ?? throw AppException.NotFound();
        if (survey.Status != SurveyStatus.Draft) throw AppException.Conflict(ErrorCodes.SurveyNotEditable);

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        Db.SurveyQuestions.RemoveRange(survey.Questions);
        await Db.SaveChangesAsync(ct);
        survey.Questions.Clear();
        await ApplyAsync(survey, r, ct);
        // New questions carry client-side keys, so they must be marked as inserts explicitly.
        Db.SurveyQuestions.AddRange(survey.Questions);
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>
    /// Publishes (or reopens) the survey. Students of the group are notified only on the first publication.
    /// </summary>
    [HttpPost("{id:guid}/open")]
    public async Task<SurveyDetailDto> Open(Guid id, CancellationToken ct)
    {
        var survey = await Db.Surveys.Include(s => s.Questions).FirstOrDefaultAsync(s => s.Id == id, ct)
                     ?? throw AppException.NotFound();
        if (survey.Status == SurveyStatus.Open) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        if (survey.Questions.Count == 0) throw AppException.Conflict(ErrorCodes.SurveyHasNoQuestions);
        if (survey.ClosesAt is not null && survey.ClosesAt <= time.UtcNow) throw AppException.Conflict(ErrorCodes.SurveyWindowEnded);
        await RequireActiveGroupAsync(survey.GroupId, "groupId", ct);

        survey.Status = SurveyStatus.Open;
        if (survey.PublishedAt is null)
        {
            survey.PublishedAt = time.UtcNow;
            await notifications.NotifyGroupAsync(survey.GroupId, NotificationType.SurveyAssigned,
                new SurveyAssignedPayload(survey.Id, survey.Title, survey.Type), ct);
        }
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpPost("{id:guid}/close")]
    public async Task<SurveyDetailDto> Close(Guid id, CancellationToken ct)
    {
        var survey = await Db.Surveys.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
        if (survey.Status != SurveyStatus.Open) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        survey.Status = SurveyStatus.Closed;
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var survey = await Db.Surveys.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
        if (survey.Status != SurveyStatus.Draft || await Db.SurveyResponses.AnyAsync(r => r.SurveyId == id, ct))
            throw AppException.Conflict(ErrorCodes.SurveyNotEditable);
        Db.Surveys.Remove(survey);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Surveys are not anonymous: the manager sees each respondent and their answers.</summary>
    [HttpGet("{id:guid}/responses")]
    public async Task<List<SurveyResponseDto>> Responses(Guid id, CancellationToken ct)
    {
        if (!await Db.Surveys.AnyAsync(s => s.Id == id, ct)) throw AppException.NotFound();
        return await LoadResponses(Db, id, ct);
    }

    internal static async Task<List<SurveyResponseDto>> LoadResponses(AppDbContext db, Guid surveyId, CancellationToken ct)
    {
        var responses = await db.SurveyResponses.AsNoTracking().AsSplitQuery()
            .Include(r => r.Student).ThenInclude(s => s!.Group)
            .Include(r => r.Answers).ThenInclude(a => a.Option)
            .Where(r => r.SurveyId == surveyId)
            .OrderBy(r => r.Student!.LastName).ThenBy(r => r.Student!.FirstName)
            .ToListAsync(ct);
        return responses.Select(r => new SurveyResponseDto(r.Id, r.StudentId, r.Student!.FullName, r.Student.Group!.Name,
            r.SubmittedAt,
            r.Answers.Select(a => new ResponseAnswerDto(a.QuestionId, a.IntValue, a.OptionId, a.Option?.Text, a.TextValue)).ToList()))
            .ToList();
    }

    internal static List<SurveyQuestionDto> MapQuestions(IEnumerable<SurveyQuestion> questions) =>
        questions.OrderBy(q => q.Order).Select(q => new SurveyQuestionDto(q.Id, q.Order, q.Text, q.Type, q.IsRequired,
            q.ScaleMin, q.ScaleMax, q.ScaleMinLabel, q.ScaleMaxLabel,
            q.Options.OrderBy(o => o.Order).Select(o => new SurveyOptionDto(o.Id, o.Order, o.Text)).ToList())).ToList();

    private async Task ApplyAsync(Survey survey, SurveyRequest r, CancellationToken ct)
    {
        await RequireActiveGroupAsync(r.GroupId, nameof(r.GroupId), ct);
        // A teaching evaluation is always about a specific teacher and discipline; a service survey is about neither.
        var teaching = r.Type == SurveyType.TeachingEvaluation;
        if (teaching)
        {
            if (r.TeacherId is null) throw AppException.Validation(nameof(r.TeacherId), FieldCodes.Required);
            if (r.DisciplineId is null) throw AppException.Validation(nameof(r.DisciplineId), FieldCodes.Required);
            await RequireExistsAsync<Teacher>(r.TeacherId, nameof(r.TeacherId), ct);
            await RequireExistsAsync<Discipline>(r.DisciplineId, nameof(r.DisciplineId), ct);
        }
        var opens = time.ToUtc(r.OpensAt, nameof(r.OpensAt));
        var closes = time.ToUtc(r.ClosesAt, nameof(r.ClosesAt));
        if (opens is not null && closes is not null && closes <= opens)
            throw AppException.Validation(nameof(r.ClosesAt), FieldCodes.EndBeforeStart);

        survey.Type = r.Type!.Value;
        survey.Title = r.Title.Trim();
        survey.Description = Clean(r.Description);
        survey.GroupId = r.GroupId!.Value;
        survey.TeacherId = teaching ? r.TeacherId : null;
        survey.DisciplineId = teaching ? r.DisciplineId : null;
        survey.OpensAt = opens;
        survey.ClosesAt = closes;

        var questions = r.Questions ?? [];
        for (var i = 0; i < questions.Count; i++)
            survey.Questions.Add(SurveyQuestionBuilder.BuildSurveyQuestion(questions[i], i, $"questions[{i}]"));
    }
}

/// <param name="Title">Optional override; defaults to the template title.</param>
/// <param name="Description">Optional override; null keeps the template description; empty string clears it.</param>
public record CreateSurveyFromTemplateRequest(
    [Required(ErrorMessage = FieldCodes.Required)] Guid TemplateId,
    [Required(ErrorMessage = FieldCodes.Required)] Guid? GroupId,
    Guid? TeacherId,
    Guid? DisciplineId,
    DateTime? OpensAt,
    DateTime? ClosesAt,
    [MaxLength(300, ErrorMessage = FieldCodes.MaxLength)] string? Title = null,
    [MaxLength(4000, ErrorMessage = FieldCodes.MaxLength)] string? Description = null);
