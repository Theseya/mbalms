using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MbaLms.Api.Tests;

public class SurveyChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    private static readonly string[] SurveyFields =
        ["type", "title", "group", "teacher", "discipline", "opensAt", "closesAt", "status",
            "description", "questionCount", "questionsStructure"];

    [Fact]
    public async Task Survey_lifecycle_and_template_paths_are_logged_without_answer_payloads()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var survey = await CreateSurveyAsync(manager, group.Id, "Опрос журнала", "Секретное описание");

        var afterCreate = await LogsFor(manager, ChangeLogEntityType.Survey, survey.Id);
        Assert.Single(afterCreate);
        Assert.Equal(ChangeLogAction.Created, afterCreate[0].Action);
        Assert.Equal(ApiFactory.ManagerEmail, afterCreate[0].ActorEmail);
        Assert.Null(afterCreate[0].Source);
        Assert.All(afterCreate[0].Changes, c => Assert.Contains(c.Field, SurveyFields));
        Assert.DoesNotContain("Секретное", JsonSerializer.Serialize(afterCreate[0].Changes));
        Assert.DoesNotContain("Оцените по шкале", JsonSerializer.Serialize(afterCreate[0].Changes));

        await manager.PutJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}", SurveyBody(group.Id,
            title: "Опрос журнала 2", description: "Другое описание",
            questions: SampleQuestions("Новый текст вопроса")));
        var afterUpdate = await LogsFor(manager, ChangeLogEntityType.Survey, survey.Id);
        Assert.Equal(2, afterUpdate.Count);
        Assert.Equal(ChangeLogAction.Updated, afterUpdate[1].Action);
        Assert.Contains(afterUpdate[1].Changes, c => c.Field == "title");
        Assert.Contains(afterUpdate[1].Changes, c => c.Field == "description" && c.New?.GetString() == "changed");
        Assert.Contains(afterUpdate[1].Changes, c => c.Field == "questionsStructure" && c.New?.GetString() == "changed");
        Assert.DoesNotContain("Новый текст", JsonSerializer.Serialize(afterUpdate[1].Changes));

        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");
        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/close");
        var afterClose = await LogsFor(manager, ChangeLogEntityType.Survey, survey.Id);
        Assert.Equal(
            [ChangeLogAction.Created, ChangeLogAction.Updated, ChangeLogAction.Opened, ChangeLogAction.Closed],
            afterClose.Select(e => e.Action).ToArray());

        // Reopen then we need draft for delete — create a fresh draft for delete path.
        var draft = await CreateSurveyAsync(manager, group.Id, "На удаление");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/surveys/{draft.Id}")).StatusCode);
        var deletedLogs = await LogsFor(manager, ChangeLogEntityType.Survey, draft.Id);
        Assert.Equal(2, deletedLogs.Count);
        Assert.Equal(ChangeLogAction.Deleted, deletedLogs[^1].Action);
        Assert.False(deletedLogs[^1].EntityExists);
        Assert.Equal("На удаление", deletedLogs[^1].EntityTitle);

        // Save as template: only template Created with source; survey unchanged in log count for Updated.
        var surveyLogsBeforeSave = (await LogsFor(manager, ChangeLogEntityType.Survey, survey.Id)).Count;
        var template = await manager.PostJsonAsync<SurveyTemplateDetailDto>(
            $"/api/manager/surveys/{survey.Id}/save-as-template", new { }, HttpStatusCode.Created);
        Assert.Equal(surveyLogsBeforeSave, (await LogsFor(manager, ChangeLogEntityType.Survey, survey.Id)).Count);
        var tmplCreated = Assert.Single(await LogsFor(manager, ChangeLogEntityType.SurveyTemplate, template.Id));
        Assert.Equal(ChangeLogAction.Created, tmplCreated.Action);
        Assert.Equal("fromSurvey", tmplCreated.Source);
        Assert.Equal(ApiFactory.ManagerEmail, tmplCreated.ActorEmail);
        Assert.DoesNotContain("Оцените", JsonSerializer.Serialize(tmplCreated.Changes));

        // From template: survey Created with source.
        var fromTpl = await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys/from-template", new
        {
            templateId = template.Id,
            groupId = group.Id,
            title = "Из шаблона QA"
        }, HttpStatusCode.Created);
        var fromLog = Assert.Single(await LogsFor(manager, ChangeLogEntityType.Survey, fromTpl.Id));
        Assert.Equal(ChangeLogAction.Created, fromLog.Action);
        Assert.Equal("fromTemplate", fromLog.Source);

        // Template update / delete
        await manager.PutJsonAsync<SurveyTemplateDetailDto>($"/api/manager/survey-templates/{template.Id}", new
        {
            type = "ServiceSurvey",
            title = "Шаблон изменён",
            description = "x",
            questions = SampleQuestions("Структура шаблона")
        });
        var tmplLogs = await LogsFor(manager, ChangeLogEntityType.SurveyTemplate, template.Id);
        Assert.Equal(2, tmplLogs.Count);
        Assert.Equal(ChangeLogAction.Updated, tmplLogs[1].Action);
        Assert.Contains(tmplLogs[1].Changes, c => c.Field == "questionsStructure");

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/survey-templates/{template.Id}")).StatusCode);
        var tmplDeleted = await LogsFor(manager, ChangeLogEntityType.SurveyTemplate, template.Id);
        Assert.Equal(ChangeLogAction.Deleted, tmplDeleted[^1].Action);
        Assert.False(tmplDeleted[^1].EntityExists);

        // No answer / response fields anywhere in survey log payloads for this suite's entities.
        foreach (var entry in afterClose.Concat(deletedLogs).Concat([fromLog]))
        {
            var json = JsonSerializer.Serialize(entry.Changes);
            Assert.DoesNotContain("textValue", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SurveyAnswer", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Всё отлично", json);
        }
    }

    [Fact]
    public async Task Survey_no_op_and_repeat_status_do_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var survey = await CreateSurveyAsync(manager, group.Id, "NoOp");
        var body = SurveyBody(group.Id, title: survey.Title, description: survey.Description);
        var before = await CountFor(manager, ChangeLogEntityType.Survey, survey.Id);

        await manager.PutJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}", body);
        Assert.Equal(before, await CountFor(manager, ChangeLogEntityType.Survey, survey.Id));

        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");
        var afterOpen = await CountFor(manager, ChangeLogEntityType.Survey, survey.Id);
        Assert.Equal(before + 1, afterOpen);

        var again = await manager.PostAsync($"/api/manager/surveys/{survey.Id}/open");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(afterOpen, await CountFor(manager, ChangeLogEntityType.Survey, survey.Id));

        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/close");
        var afterClose = await CountFor(manager, ChangeLogEntityType.Survey, survey.Id);
        Assert.Equal(afterOpen + 1, afterClose);

        var closeAgain = await manager.PostAsync($"/api/manager/surveys/{survey.Id}/close");
        Assert.Equal(HttpStatusCode.Conflict, closeAgain.StatusCode);
        Assert.Equal(afterClose, await CountFor(manager, ChangeLogEntityType.Survey, survey.Id));
    }

    [Fact]
    public async Task Failed_survey_create_does_not_log()
    {
        var manager = await ManagerAsync();
        var before = await TotalCount(manager);
        var res = await manager.PostAsync("/api/manager/surveys", new
        {
            type = "ServiceSurvey", title = "", groupId = Guid.CreateVersion7(),
            questions = SampleQuestions()
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, await TotalCount(manager));
    }

    [Fact]
    public async Task Failed_SaveChanges_on_survey_create_rolls_back_entity_and_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var beforeLogs = await TotalCount(manager);
        var beforeSurveys = await CountSurveysAsync();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/surveys", SurveyBody(group.Id, title: "Atomic survey"));
            // FK violation is mapped to 409 by AppExceptionHandler; still proves DB rejected SaveChanges.
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Factory.SaveFail.FailedSqlState);
        Assert.Equal(beforeLogs, await TotalCount(manager));
        Assert.Equal(beforeSurveys, await CountSurveysAsync());
        Assert.False(await NewDb().Surveys.AnyAsync(s => s.Title == "Atomic survey"));
    }

    [Fact]
    public async Task Two_managers_produce_distinct_survey_actors()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var survey = await CreateSurveyAsync(manager, group.Id, "Shared survey");

        var other = await CreateOtherManagerClientAsync();
        await other.PutJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}",
            SurveyBody(group.Id, title: "Shared by other"));

        var logs = await LogsFor(manager, ChangeLogEntityType.Survey, survey.Id);
        Assert.Contains(logs, e => e.Action == ChangeLogAction.Created && e.ActorEmail == ApiFactory.ManagerEmail);
        Assert.Contains(logs, e => e.Action == ChangeLogAction.Updated && e.ActorEmail != ApiFactory.ManagerEmail);
        Assert.Equal(2, logs.Select(e => e.ActorUserId).Distinct().Count());
    }

    private static async Task<List<ChangeLogListItemDto>> LogsFor(ApiClient manager, ChangeLogEntityType type, Guid entityId)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            $"/api/manager/change-log?entityType={type}&page=1&pageSize=100");
        return page.Items.Where(i => i.EntityId == entityId).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
    }

    private static async Task<int> CountFor(ApiClient manager, ChangeLogEntityType type, Guid entityId) =>
        (await LogsFor(manager, entityId: entityId, type: type)).Count;

    private static async Task<int> TotalCount(ApiClient manager)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>("/api/manager/change-log?page=1&pageSize=1");
        return page.Total;
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private async Task<int> CountSurveysAsync()
    {
        await using var db = NewDb();
        return await db.Surveys.CountAsync();
    }

    private async Task<ApiClient> CreateOtherManagerClientAsync()
    {
        var email = $"{Unique("mgr")}@test.local";
        const string password = "Manager-Test-2";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(user, Roles.Manager)).Succeeded);
        }
        var client = NewClient();
        await client.LoginAsync(email, password);
        return client;
    }

    private static object[] SampleQuestions(string scaleText = "Оцените по шкале") =>
    [
        new { text = scaleText, type = "Scale", isRequired = true, scaleMin = 1, scaleMax = 5 },
        new { text = "Выберите вариант", type = "SingleChoice", isRequired = true, options = new[] { "Да", "Нет" } },
        new { text = "Комментарий", type = "Text", isRequired = false }
    ];

    private static object SurveyBody(Guid groupId, string title = "Опрос", string? description = "Описание",
        object[]? questions = null) => new
    {
        type = "ServiceSurvey",
        title,
        description,
        groupId,
        questions = questions ?? SampleQuestions()
    };

    private static Task<SurveyDetailDto> CreateSurveyAsync(ApiClient manager, Guid groupId, string title,
        string? description = "Описание") =>
        manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys",
            SurveyBody(groupId, title, description), HttpStatusCode.Created);
}
