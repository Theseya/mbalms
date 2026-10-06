using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class SurveyTemplateTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Save_as_template_copies_structure_without_touching_responses()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        var survey = await CreateSurveyAsync(manager, group.Id, SurveyType.ServiceSurvey);
        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");

        var detail = await student.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        await (await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", new
        {
            answers = new object[]
            {
                new { questionId = detail.Questions[0].Id, intValue = 4 },
                new { questionId = detail.Questions[1].Id, optionId = detail.Questions[1].Options[0].Id },
                new { questionId = detail.Questions[2].Id, textValue = "Ответ" }
            }
        })).EnsureStatusAsync(HttpStatusCode.Created);

        var before = await manager.GetJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}");
        Assert.Equal(1, before.ResponseCount);

        var template = await manager.PostJsonAsync<SurveyTemplateDetailDto>(
            $"/api/manager/surveys/{survey.Id}/save-as-template", new { }, HttpStatusCode.Created);

        Assert.Equal(survey.Type, template.Type);
        Assert.Equal(survey.Title, template.Title);
        Assert.Equal(survey.Description, template.Description);
        Assert.Equal(3, template.Questions.Count);
        Assert.All(template.Questions, q => Assert.NotEqual(survey.Questions.First(sq => sq.Order == q.Order).Id, q.Id));
        Assert.Equal(2, template.Questions.Single(q => q.Type == QuestionType.SingleChoice).Options.Count);

        var after = await manager.GetJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}");
        Assert.Equal(1, after.ResponseCount);
        Assert.Equal(before.Questions.Select(q => q.Id), after.Questions.Select(q => q.Id));
        Assert.Equal(SurveyStatus.Open, after.Status);
    }

    [Theory]
    [InlineData(SurveyType.TeachingEvaluation)]
    [InlineData(SurveyType.ServiceSurvey)]
    public async Task Create_from_template_makes_independent_draft(SurveyType type)
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = type == SurveyType.TeachingEvaluation ? await CreateTeacherAsync(manager) : null;
        var discipline = type == SurveyType.TeachingEvaluation ? await CreateDisciplineAsync(manager) : null;

        var template = await manager.PostJsonAsync<SurveyTemplateDetailDto>("/api/manager/survey-templates", new
        {
            type = type.ToString(),
            title = Unique("Шаблон"),
            description = "Описание шаблона",
            questions = SampleQuestions()
        }, HttpStatusCode.Created);

        var created = await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys/from-template", new
        {
            templateId = template.Id,
            groupId = group.Id,
            teacherId = teacher?.Id,
            disciplineId = discipline?.Id,
            opensAt = "2030-01-01T10:00",
            closesAt = "2030-01-31T18:00"
        }, HttpStatusCode.Created);

        Assert.Equal(SurveyStatus.Draft, created.Status);
        Assert.Equal(template.Type, created.Type);
        Assert.Equal(template.Title, created.Title);
        Assert.Equal(template.Description, created.Description);
        Assert.Equal(group.Id, created.GroupId);
        Assert.Equal(teacher?.Id, created.TeacherId);
        Assert.Equal(discipline?.Id, created.DisciplineId);
        Assert.Equal(0, created.ResponseCount);
        Assert.Equal(3, created.Questions.Count);
        Assert.All(created.Questions, q =>
            Assert.DoesNotContain(template.Questions, tq => tq.Id == q.Id));
        Assert.All(created.Questions.SelectMany(q => q.Options), o =>
            Assert.DoesNotContain(template.Questions.SelectMany(tq => tq.Options), to => to.Id == o.Id));
    }

    [Fact]
    public async Task Teaching_from_template_requires_teacher_and_discipline()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var template = await manager.PostJsonAsync<SurveyTemplateDetailDto>("/api/manager/survey-templates", new
        {
            type = SurveyType.TeachingEvaluation.ToString(),
            title = Unique("TE"),
            questions = SampleQuestions()
        }, HttpStatusCode.Created);

        var res = await manager.PostAsync("/api/manager/surveys/from-template", new
        {
            templateId = template.Id,
            groupId = group.Id
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Template_crud_and_delete_do_not_affect_created_surveys()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var template = await manager.PostJsonAsync<SurveyTemplateDetailDto>("/api/manager/survey-templates", new
        {
            type = SurveyType.ServiceSurvey.ToString(),
            title = Unique("CRUD"),
            description = "A",
            questions = SampleQuestions()
        }, HttpStatusCode.Created);

        var survey = await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys/from-template", new
        {
            templateId = template.Id,
            groupId = group.Id
        }, HttpStatusCode.Created);
        var surveyQuestionIds = survey.Questions.Select(q => q.Id).ToList();

        var updated = await manager.PutJsonAsync<SurveyTemplateDetailDto>($"/api/manager/survey-templates/{template.Id}", new
        {
            type = SurveyType.ServiceSurvey.ToString(),
            title = "Изменённый шаблон",
            description = "B",
            questions = new object[]
            {
                new { text = "Только один вопрос", type = "Text", isRequired = true }
            }
        });
        Assert.Equal("Изменённый шаблон", updated.Title);
        Assert.Single(updated.Questions);

        var list = await manager.GetJsonAsync<List<SurveyTemplateListItemDto>>("/api/manager/survey-templates");
        Assert.Contains(list, t => t.Id == template.Id && t.QuestionCount == 1);

        await (await manager.DeleteAsync($"/api/manager/survey-templates/{template.Id}"))
            .EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound,
            (await manager.GetAsync($"/api/manager/survey-templates/{template.Id}")).StatusCode);

        var stillThere = await manager.GetJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}");
        Assert.Equal(survey.Title, stillThere.Title);
        Assert.Equal(surveyQuestionIds, stillThere.Questions.Select(q => q.Id));
        Assert.Equal(3, stillThere.Questions.Count);
    }

    [Fact]
    public async Task Templates_require_manager_role()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        var anonymous = await AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/manager/survey-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/survey-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.PostAsync("/api/manager/survey-templates", new
            {
                type = "ServiceSurvey", title = "X", questions = SampleQuestions()
            })).StatusCode);
    }

    private static object[] SampleQuestions() =>
    [
        new { text = "Оцените по шкале", type = "Scale", isRequired = true, scaleMin = 1, scaleMax = 5,
            scaleMinLabel = "Плохо", scaleMaxLabel = "Отлично" },
        new { text = "Выберите вариант", type = "SingleChoice", isRequired = true, options = new[] { "Да", "Нет" } },
        new { text = "Комментарий", type = "Text", isRequired = false }
    ];

    private static async Task<SurveyDetailDto> CreateSurveyAsync(ApiClient manager, Guid groupId, SurveyType type)
    {
        var teacher = type == SurveyType.TeachingEvaluation ? await CreateTeacherAsync(manager) : null;
        var discipline = type == SurveyType.TeachingEvaluation ? await CreateDisciplineAsync(manager) : null;
        return await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys", new
        {
            type = type.ToString(),
            title = Unique("Опрос"),
            description = "Описание",
            groupId,
            teacherId = teacher?.Id,
            disciplineId = discipline?.Id,
            questions = SampleQuestions()
        }, HttpStatusCode.Created);
    }
}
