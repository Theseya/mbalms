using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record SurveyTemplateListItemDto(Guid Id, SurveyType Type, string Title, int QuestionCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record SurveyTemplateDetailDto(Guid Id, SurveyType Type, string Title, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<SurveyQuestionDto> Questions);

public record SurveyTemplateRequest(
    [Required(ErrorMessage = FieldCodes.Required)] SurveyType? Type,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(300, ErrorMessage = FieldCodes.MaxLength)] string Title,
    [MaxLength(4000, ErrorMessage = FieldCodes.MaxLength)] string? Description,
    [MaxLength(50, ErrorMessage = FieldCodes.MaxLength)] List<SurveyQuestionRequest>? Questions);

[Route("api/manager/survey-templates")]
[ApiController]
public class SurveyTemplatesController(AppDbContext db, AppTime time) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<SurveyTemplateListItemDto>> List(CancellationToken ct = default)
    {
        return await Db.SurveyTemplates.AsNoTracking()
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new SurveyTemplateListItemDto(t.Id, t.Type, t.Title, t.Questions.Count, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<SurveyTemplateDetailDto> Get(Guid id, CancellationToken ct) =>
        await LoadDetail(Db, id, ct);

    [HttpPost]
    public async Task<ActionResult<SurveyTemplateDetailDto>> Create(SurveyTemplateRequest r, CancellationToken ct)
    {
        var now = time.UtcNow;
        var template = new SurveyTemplate
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            UpdatedAt = now,
            Title = r.Title
        };
        Apply(template, r);
        Db.SurveyTemplates.Add(template);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = template.Id }, await LoadDetail(Db, template.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<SurveyTemplateDetailDto> Update(Guid id, SurveyTemplateRequest r, CancellationToken ct)
    {
        var template = await Db.SurveyTemplates.Include(t => t.Questions).ThenInclude(q => q.Options)
                           .FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw AppException.NotFound();

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        Db.SurveyTemplateQuestions.RemoveRange(template.Questions);
        await Db.SaveChangesAsync(ct);
        template.Questions.Clear();
        Apply(template, r);
        template.UpdatedAt = time.UtcNow;
        Db.SurveyTemplateQuestions.AddRange(template.Questions);
        await Db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadDetail(Db, id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var template = await Db.SurveyTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw AppException.NotFound();
        Db.SurveyTemplates.Remove(template);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    internal static async Task<SurveyTemplateDetailDto> LoadDetail(AppDbContext db, Guid id, CancellationToken ct)
    {
        var t = await db.SurveyTemplates.AsNoTracking().AsSplitQuery()
                    .Include(x => x.Questions).ThenInclude(q => q.Options)
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw AppException.NotFound();
        return new SurveyTemplateDetailDto(t.Id, t.Type, t.Title, t.Description, t.CreatedAt, t.UpdatedAt,
            t.Questions.OrderBy(q => q.Order).Select(q => new SurveyQuestionDto(q.Id, q.Order, q.Text, q.Type, q.IsRequired,
                q.ScaleMin, q.ScaleMax, q.ScaleMinLabel, q.ScaleMaxLabel,
                q.Options.OrderBy(o => o.Order).Select(o => new SurveyOptionDto(o.Id, o.Order, o.Text)).ToList())).ToList());
    }

    private static void Apply(SurveyTemplate template, SurveyTemplateRequest r)
    {
        template.Type = r.Type!.Value;
        template.Title = r.Title.Trim();
        template.Description = Clean(r.Description);
        var questions = r.Questions ?? [];
        for (var i = 0; i < questions.Count; i++)
            template.Questions.Add(SurveyQuestionBuilder.BuildTemplateQuestion(questions[i], i, $"questions[{i}]"));
    }
}
