using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Tests;

/// <summary>
/// Proves Survey/SurveyTemplate Update (two SaveChanges in one explicit transaction) rolls back
/// fully when the second SaveChanges hits a real Postgres error after the first succeeded.
/// </summary>
public class SurveyChangeLogAtomicityTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Failed_second_SaveChanges_on_survey_update_rolls_back_questions_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var survey = await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys", new
        {
            type = "ServiceSurvey",
            title = "Atomic survey original",
            description = "Original description",
            groupId = group.Id,
            questions = OriginalQuestions("Оригинальный шкальный", "Да", "Нет")
        }, HttpStatusCode.Created);

        await using (var beforeDb = NewDb())
        {
            var snap = await LoadSurveyGraphAsync(beforeDb, survey.Id);
            Assert.Equal(3, snap.Questions.Count);
            Assert.Equal(2, snap.Questions.Single(q => q.Type == QuestionType.SingleChoice).Options.Count);
        }

        var original = await CaptureSurveyAsync(survey.Id);
        var historyBefore = await CaptureLogHistoryAsync(ChangeLogEntityType.Survey, survey.Id);
        Assert.Single(historyBefore);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/surveys/{survey.Id}", new
            {
                type = "ServiceSurvey",
                title = "SHOULD-NOT-PERSIST-TITLE",
                description = "SHOULD-NOT-PERSIST-DESC",
                groupId = group.Id,
                questions = new object[]
                {
                    new
                    {
                        text = "SHOULD-NOT-PERSIST-Q", type = "Text", isRequired = true
                    }
                }
            });
            Assert.False(res.IsSuccessStatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawSuccessfulIntermediateSaveChanges,
            "First SaveChanges (delete old questions) must complete successfully inside the transaction.");
        Assert.Equal(3, Factory.SaveFail.IntermediateDeletedQuestionCount);
        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog,
            "Second SaveChanges must stage Survey mutation + ChangeLogEntry together.");
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure,
            "Failure must come from a database command during the second SaveChanges, not a pre-SQL throw.");
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        var restored = await LoadSurveyGraphAsync(db, survey.Id);
        AssertSurveyUnchanged(original, restored);

        Assert.False(await db.SurveyQuestions.AnyAsync(q => q.Text == "SHOULD-NOT-PERSIST-Q"));
        Assert.False(await db.Surveys.AnyAsync(s => s.Title == "SHOULD-NOT-PERSIST-TITLE"));

        var historyAfter = await CaptureLogHistoryAsync(ChangeLogEntityType.Survey, survey.Id);
        AssertLogHistoryUnchanged(historyBefore, historyAfter);
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == survey.Id && e.Action == ChangeLogAction.Updated));
    }

    [Fact]
    public async Task Failed_second_SaveChanges_on_template_update_rolls_back_questions_and_change_log()
    {
        var manager = await ManagerAsync();
        var template = await manager.PostJsonAsync<SurveyTemplateDetailDto>("/api/manager/survey-templates", new
        {
            type = "ServiceSurvey",
            title = "Atomic template original",
            description = "Template original description",
            questions = OriginalQuestions("Шаблонный шкальный", "A", "B")
        }, HttpStatusCode.Created);

        var original = await CaptureTemplateAsync(template.Id);
        var historyBefore = await CaptureLogHistoryAsync(ChangeLogEntityType.SurveyTemplate, template.Id);
        Assert.Single(historyBefore);
        var updatedAtBefore = original.UpdatedAt;

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync($"/api/manager/survey-templates/{template.Id}", new
            {
                type = "ServiceSurvey",
                title = "SHOULD-NOT-PERSIST-TMPL",
                description = "SHOULD-NOT-PERSIST-TMPL-DESC",
                questions = new object[]
                {
                    new
                    {
                        text = "SHOULD-NOT-PERSIST-TMPL-Q", type = "Text", isRequired = true
                    }
                }
            });
            Assert.False(res.IsSuccessStatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawSuccessfulIntermediateSaveChanges,
            "First SaveChanges (delete old template questions) must complete successfully inside the transaction.");
        Assert.Equal(3, Factory.SaveFail.IntermediateDeletedQuestionCount);
        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog,
            "Second SaveChanges must stage SurveyTemplate mutation + ChangeLogEntry together.");
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure,
            "Failure must come from a database command during the second SaveChanges, not a pre-SQL throw.");
        Assert.NotNull(Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        var restored = await LoadTemplateGraphAsync(db, template.Id);
        AssertTemplateUnchanged(original, restored);
        Assert.Equal(updatedAtBefore, restored.UpdatedAt);

        Assert.False(await db.SurveyTemplateQuestions.AnyAsync(q => q.Text == "SHOULD-NOT-PERSIST-TMPL-Q"));
        Assert.False(await db.SurveyTemplates.AnyAsync(t => t.Title == "SHOULD-NOT-PERSIST-TMPL"));

        var historyAfter = await CaptureLogHistoryAsync(ChangeLogEntityType.SurveyTemplate, template.Id);
        AssertLogHistoryUnchanged(historyBefore, historyAfter);
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityId == template.Id && e.Action == ChangeLogAction.Updated));
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private static object[] OriginalQuestions(string scaleText, string optA, string optB) =>
    [
        new { text = scaleText, type = "Scale", isRequired = true, scaleMin = 1, scaleMax = 5 },
        new { text = "Выберите вариант", type = "SingleChoice", isRequired = true, options = new[] { optA, optB } },
        new { text = "Комментарий", type = "Text", isRequired = false }
    ];

    private async Task<SurveySnap> CaptureSurveyAsync(Guid id)
    {
        await using var db = NewDb();
        return await LoadSurveyGraphAsync(db, id);
    }

    private async Task<TemplateSnap> CaptureTemplateAsync(Guid id)
    {
        await using var db = NewDb();
        return await LoadTemplateGraphAsync(db, id);
    }

    private static async Task<SurveySnap> LoadSurveyGraphAsync(AppDbContext db, Guid id)
    {
        var s = await db.Surveys.AsNoTracking().AsSplitQuery()
            .Include(x => x.Questions).ThenInclude(q => q.Options)
            .SingleAsync(x => x.Id == id);
        return new SurveySnap(
            s.Id, s.Type, s.Title, s.Description, s.GroupId, s.Status, s.TeacherId, s.DisciplineId,
            s.OpensAt, s.ClosesAt,
            s.Questions.OrderBy(q => q.Order).Select(q => new QuestionSnap(
                q.Id, q.Order, q.Text, q.Type, q.IsRequired, q.ScaleMin, q.ScaleMax,
                q.ScaleMinLabel, q.ScaleMaxLabel,
                q.Options.OrderBy(o => o.Order).Select(o => new OptionSnap(o.Id, o.Order, o.Text)).ToList()
            )).ToList());
    }

    private static async Task<TemplateSnap> LoadTemplateGraphAsync(AppDbContext db, Guid id)
    {
        var t = await db.SurveyTemplates.AsNoTracking().AsSplitQuery()
            .Include(x => x.Questions).ThenInclude(q => q.Options)
            .SingleAsync(x => x.Id == id);
        return new TemplateSnap(
            t.Id, t.Type, t.Title, t.Description, t.CreatedAt, t.UpdatedAt,
            t.Questions.OrderBy(q => q.Order).Select(q => new QuestionSnap(
                q.Id, q.Order, q.Text, q.Type, q.IsRequired, q.ScaleMin, q.ScaleMax,
                q.ScaleMinLabel, q.ScaleMaxLabel,
                q.Options.OrderBy(o => o.Order).Select(o => new OptionSnap(o.Id, o.Order, o.Text)).ToList()
            )).ToList());
    }

    private async Task<List<LogSnap>> CaptureLogHistoryAsync(ChangeLogEntityType type, Guid entityId)
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.AsNoTracking()
            .Where(e => e.EntityType == type && e.EntityId == entityId)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Select(e => new LogSnap(e.Id, e.Action, e.OccurredAt, e.ChangesJson, e.ContextJson, e.ActorUserId))
            .ToListAsync();
    }

    private static void AssertSurveyUnchanged(SurveySnap original, SurveySnap restored)
    {
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.GroupId, restored.GroupId);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.TeacherId, restored.TeacherId);
        Assert.Equal(original.DisciplineId, restored.DisciplineId);
        Assert.Equal(original.OpensAt, restored.OpensAt);
        Assert.Equal(original.ClosesAt, restored.ClosesAt);
        AssertQuestionsRestored(original.Questions, restored.Questions);
    }

    private static void AssertTemplateUnchanged(TemplateSnap original, TemplateSnap restored)
    {
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt);
        AssertQuestionsRestored(original.Questions, restored.Questions);
    }

    private static void AssertQuestionsRestored(List<QuestionSnap> original, List<QuestionSnap> restored)
    {
        Assert.Equal(original.Count, restored.Count);
        for (var i = 0; i < original.Count; i++)
        {
            var o = original[i];
            var r = restored[i];
            Assert.Equal(o.Id, r.Id);
            Assert.Equal(o.Order, r.Order);
            Assert.Equal(o.Text, r.Text);
            Assert.Equal(o.Type, r.Type);
            Assert.Equal(o.IsRequired, r.IsRequired);
            Assert.Equal(o.ScaleMin, r.ScaleMin);
            Assert.Equal(o.ScaleMax, r.ScaleMax);
            Assert.Equal(o.ScaleMinLabel, r.ScaleMinLabel);
            Assert.Equal(o.ScaleMaxLabel, r.ScaleMaxLabel);
            Assert.Equal(o.Options.Count, r.Options.Count);
            for (var j = 0; j < o.Options.Count; j++)
            {
                Assert.Equal(o.Options[j].Id, r.Options[j].Id);
                Assert.Equal(o.Options[j].Order, r.Options[j].Order);
                Assert.Equal(o.Options[j].Text, r.Options[j].Text);
            }
        }
    }

    private static void AssertLogHistoryUnchanged(List<LogSnap> before, List<LogSnap> after)
    {
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            Assert.Equal(before[i].Action, after[i].Action);
            Assert.Equal(before[i].OccurredAt, after[i].OccurredAt);
            Assert.Equal(before[i].ChangesJson, after[i].ChangesJson);
            Assert.Equal(before[i].ContextJson, after[i].ContextJson);
            Assert.Equal(before[i].ActorUserId, after[i].ActorUserId);
        }

        // Payload still must not contain question/option text from the attempted update.
        var json = JsonSerializer.Serialize(after);
        Assert.DoesNotContain("SHOULD-NOT-PERSIST", json);
    }

    private sealed record OptionSnap(Guid Id, int Order, string Text);

    private sealed record QuestionSnap(
        Guid Id, int Order, string Text, QuestionType Type, bool IsRequired,
        int? ScaleMin, int? ScaleMax, string? ScaleMinLabel, string? ScaleMaxLabel,
        List<OptionSnap> Options);

    private sealed record SurveySnap(
        Guid Id, SurveyType Type, string Title, string? Description, Guid GroupId, SurveyStatus Status,
        Guid? TeacherId, Guid? DisciplineId, DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt,
        List<QuestionSnap> Questions);

    private sealed record TemplateSnap(
        Guid Id, SurveyType Type, string Title, string? Description,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<QuestionSnap> Questions);

    private sealed record LogSnap(
        Guid Id, ChangeLogAction Action, DateTimeOffset OccurredAt,
        string ChangesJson, string ContextJson, Guid? ActorUserId);
}
