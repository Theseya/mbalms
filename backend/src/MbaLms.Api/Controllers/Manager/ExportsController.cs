using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Manager-only .xlsx exports. Each export contains only the columns needed for the task
/// (no password hashes, user ids or other account data).
/// </summary>
[Route("api/manager/exports")]
public class ExportsController(AppDbContext db, AppTime time) : ManagerControllerBase(db)
{
    [HttpGet("students")]
    public async Task<IActionResult> Students([FromQuery] Guid? groupId, [FromQuery] bool includeArchived = false,
        [FromQuery] [MaxLength(Paging.MaxSearchLength, ErrorMessage = FieldCodes.MaxLength)] string? search = null,
        [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var q = StudentsController.Filter(Db.Students.AsNoTracking().Include(s => s.Group), groupId, includeArchived, search);
        var rows = await q.OrderBy(s => s.Group!.Name).ThenBy(s => s.LastName).ThenBy(s => s.FirstName).ToListAsync(ct);

        return Xlsx("students", t["sheet.students"], [
            new ExcelColumn<Student>(t["col.lastName"], s => s.LastName),
            new(t["col.firstName"], s => s.FirstName),
            new(t["col.middleName"], s => s.MiddleName),
            new(t["col.email"], s => s.Email),
            new(t["col.group"], s => s.Group!.Name),
            new(t["col.groupStatus"], s => t[$"groupStatus.{s.Group!.Status}"])
        ], rows);
    }

    [HttpGet("groups")]
    public async Task<IActionResult> Groups([FromQuery] GroupFilter status = GroupFilter.All, [FromQuery] string? lang = null,
        CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var q = Db.Groups.AsNoTracking();
        if (status == GroupFilter.Active) q = q.Where(g => g.Status == GroupStatus.Active);
        if (status == GroupFilter.Archived) q = q.Where(g => g.Status == GroupStatus.Archived);
        var rows = await q.OrderBy(g => g.Status).ThenBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, g.StartDate, g.EndDate, g.Status, g.ArchivedAt, g.Students.Count))
            .ToListAsync(ct);

        return Xlsx("groups", t["sheet.groups"], [
            new ExcelColumn<GroupDto>(t["col.name"], g => g.Name),
            new(t["col.startDate"], g => g.StartDate),
            new(t["col.endDate"], g => g.EndDate),
            new(t["col.status"], g => t[$"groupStatus.{g.Status}"]),
            new(t["col.archivedAt"], g => time.ToLocal(g.ArchivedAt)),
            new(t["col.studentCount"], g => g.StudentCount)
        ], rows);
    }

    [HttpGet("teachers")]
    public async Task<IActionResult> Teachers([FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var rows = await Db.Teachers.AsNoTracking().OrderBy(x => x.LastName).ThenBy(x => x.FirstName).ToListAsync(ct);
        return Xlsx("teachers", t["sheet.teachers"], [
            new ExcelColumn<Teacher>(t["col.lastName"], x => x.LastName),
            new(t["col.firstName"], x => x.FirstName),
            new(t["col.middleName"], x => x.MiddleName),
            new(t["col.email"], x => x.Email)
        ], rows);
    }

    [HttpGet("disciplines")]
    public async Task<IActionResult> Disciplines([FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var rows = await Db.Disciplines.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        return Xlsx("disciplines", t["sheet.disciplines"], [
            new ExcelColumn<Discipline>(t["col.name"], x => x.Name),
            new(t["col.description"], x => x.Description)
        ], rows);
    }

    [HttpGet("grades")]
    public async Task<IActionResult> Grades([FromQuery] Guid? groupId, [FromQuery] Guid? periodId, [FromQuery] Guid? disciplineId,
        [FromQuery] bool includeArchived = false, [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var q = Db.Grades.AsNoTracking().Include(g => g.Student).ThenInclude(s => s!.Group)
            .Include(g => g.Discipline).Include(g => g.Period).AsQueryable();
        if (groupId is not null) q = q.Where(g => g.Student!.GroupId == groupId);
        else if (!includeArchived) q = q.Where(g => g.Student!.Group!.Status == GroupStatus.Active);
        if (periodId is not null) q = q.Where(g => g.PeriodId == periodId);
        if (disciplineId is not null) q = q.Where(g => g.DisciplineId == disciplineId);
        var rows = await q.OrderBy(g => g.Student!.Group!.Name).ThenBy(g => g.Student!.LastName)
            .ThenBy(g => g.Student!.FirstName).ThenBy(g => g.Discipline!.Name).ToListAsync(ct);

        return Xlsx("grades", t["sheet.grades"], [
            new ExcelColumn<Grade>(t["col.student"], g => g.Student!.FullName),
            new(t["col.group"], g => g.Student!.Group!.Name),
            new(t["col.discipline"], g => g.Discipline!.Name),
            new(t["col.period"], g => g.Period!.Name),
            new(t["col.grade"], g => g.Value),
            new(t["col.status"], g => t[$"gradeStatus.{g.Status}"]),
            new(t["col.publishedAt"], g => time.ToLocal(g.PublishedAt))
        ], rows);
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule([FromQuery] Guid? groupId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] bool includeArchived = false, [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var q = LessonQueries.Filter(Db.Lessons.AsNoTracking(), time, groupId, from, to);
        if (groupId is null && !includeArchived) q = q.Where(l => l.Group!.Status == GroupStatus.Active);
        var rows = await LessonQueries.Load(q, ct);

        // Date and times are real Excel date/time cells in the application time zone, named in the headers.
        var zone = $" ({time.TimeZoneId})";
        return Xlsx("schedule", t["sheet.schedule"], [
            new ExcelColumn<Lesson>(t["col.lessonId"], l => l.Id.ToString()),
            new(t["col.date"], l => DateOnly.FromDateTime(time.ToLocal(l.StartsAt))),
            new(t["col.start"] + zone, l => TimeOnly.FromDateTime(time.ToLocal(l.StartsAt))),
            new(t["col.end"] + zone, l => TimeOnly.FromDateTime(time.ToLocal(l.EndsAt))),
            new(t["col.group"], l => l.Group!.Name),
            new(t["col.discipline"], l => l.Discipline!.Name),
            new(t["col.teacher"], l => l.Teacher!.FullName),
            new(t["col.format"], l => l.Format is null ? null : t[$"format.{l.Format}"]),
            new(t["col.location"], l => l.Location),
            new(t["col.comment"], l => l.Comment),
            new(t["col.lessonStatus"], l => t[$"lessonStatus.{l.Status}"])
        ], rows);
    }

    /// <summary>One row per respondent, one column per question.</summary>
    [HttpGet("surveys/{id:guid}")]
    public async Task<IActionResult> SurveyResults(Guid id, [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var t = ExportText.For(lang);
        var survey = await Db.Surveys.AsNoTracking().Include(s => s.Questions).ThenInclude(q => q.Options)
                         .FirstOrDefaultAsync(s => s.Id == id, ct)
                     ?? throw AppException.NotFound();
        var responses = await SurveysController.LoadResponses(Db, id, ct);
        var questions = survey.Questions.OrderBy(q => q.Order).ToList();

        var columns = new List<ExcelColumn<SurveyResponseDto>>
        {
            new(t["col.student"], r => r.StudentName),
            new(t["col.group"], r => r.GroupName),
            new(t["col.submittedAt"], r => time.ToLocal(r.SubmittedAt))
        };
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            columns.Add(new ExcelColumn<SurveyResponseDto>($"{i + 1}. {q.Text}", r =>
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                if (a is null) return null;
                return q.Type switch
                {
                    QuestionType.Scale => a.IntValue,
                    QuestionType.SingleChoice => a.OptionText,
                    _ => a.TextValue
                };
            }));
        }

        return Xlsx($"survey-{survey.Id.ToString()[..8]}", t["sheet.surveyResults"], columns, responses);
    }

    private FileContentResult Xlsx<T>(string name, string sheet, IReadOnlyList<ExcelColumn<T>> columns, IEnumerable<T> rows)
    {
        var bytes = ExcelExporter.Build(sheet, columns, rows);
        var fileName = $"{name}_{time.ToLocal(time.UtcNow):yyyy-MM-dd_HHmm}.xlsx";
        return File(bytes, ExcelExporter.ContentType, fileName);
    }
}

public sealed class ExportText
{
    private static readonly Dictionary<string, string> Ru = new()
    {
        ["sheet.students"] = "Студенты", ["sheet.groups"] = "Группы", ["sheet.teachers"] = "Преподаватели",
        ["sheet.disciplines"] = "Дисциплины", ["sheet.grades"] = "Оценки", ["sheet.schedule"] = "Расписание",
        ["sheet.surveyResults"] = "Результаты опроса",
        ["col.lastName"] = "Фамилия", ["col.firstName"] = "Имя", ["col.middleName"] = "Отчество", ["col.email"] = "Email",
        ["col.group"] = "Группа", ["col.groupStatus"] = "Статус группы", ["col.name"] = "Название",
        ["col.startDate"] = "Дата начала", ["col.endDate"] = "Дата окончания", ["col.status"] = "Статус",
        ["col.archivedAt"] = "Дата архивации", ["col.studentCount"] = "Студентов", ["col.description"] = "Описание",
        ["col.student"] = "Студент", ["col.discipline"] = "Дисциплина", ["col.period"] = "Учебный период",
        ["col.grade"] = "Оценка", ["col.publishedAt"] = "Опубликовано", ["col.date"] = "Дата", ["col.start"] = "Начало",
        ["col.end"] = "Окончание", ["col.teacher"] = "Преподаватель", ["col.format"] = "Формат",
        ["col.location"] = "Место / ссылка", ["col.comment"] = "Комментарий", ["col.submittedAt"] = "Отправлено",
        ["col.lessonId"] = "LessonId",
        ["groupStatus.Active"] = "Активная", ["groupStatus.Archived"] = "В архиве",
        ["gradeStatus.Draft"] = "Черновик", ["gradeStatus.Published"] = "Опубликована",
        ["format.Offline"] = "Очно", ["format.Online"] = "Онлайн", ["format.Hybrid"] = "Гибрид",
        ["col.lessonStatus"] = "Статус занятия",
        ["lessonStatus.Scheduled"] = "Запланировано", ["lessonStatus.Cancelled"] = "Отменено"
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["sheet.students"] = "Students", ["sheet.groups"] = "Groups", ["sheet.teachers"] = "Teachers",
        ["sheet.disciplines"] = "Disciplines", ["sheet.grades"] = "Grades", ["sheet.schedule"] = "Schedule",
        ["sheet.surveyResults"] = "Survey results",
        ["col.lastName"] = "Last name", ["col.firstName"] = "First name", ["col.middleName"] = "Middle name", ["col.email"] = "Email",
        ["col.group"] = "Group", ["col.groupStatus"] = "Group status", ["col.name"] = "Name",
        ["col.startDate"] = "Start date", ["col.endDate"] = "End date", ["col.status"] = "Status",
        ["col.archivedAt"] = "Archived at", ["col.studentCount"] = "Students", ["col.description"] = "Description",
        ["col.student"] = "Student", ["col.discipline"] = "Discipline", ["col.period"] = "Academic period",
        ["col.grade"] = "Grade", ["col.publishedAt"] = "Published at", ["col.date"] = "Date", ["col.start"] = "Start",
        ["col.end"] = "End", ["col.teacher"] = "Teacher", ["col.format"] = "Format",
        ["col.location"] = "Location / link", ["col.comment"] = "Comment", ["col.submittedAt"] = "Submitted at",
        ["col.lessonId"] = "LessonId",
        ["groupStatus.Active"] = "Active", ["groupStatus.Archived"] = "Archived",
        ["gradeStatus.Draft"] = "Draft", ["gradeStatus.Published"] = "Published",
        ["format.Offline"] = "In person", ["format.Online"] = "Online", ["format.Hybrid"] = "Hybrid",
        ["col.lessonStatus"] = "Lesson status",
        ["lessonStatus.Scheduled"] = "Scheduled", ["lessonStatus.Cancelled"] = "Cancelled"
    };

    private readonly Dictionary<string, string> _texts;
    private ExportText(Dictionary<string, string> texts) => _texts = texts;

    public static ExportText For(string? lang) =>
        new(string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? En : Ru);

    public string this[string key] => _texts.TryGetValue(key, out var v) ? v : key;
}
