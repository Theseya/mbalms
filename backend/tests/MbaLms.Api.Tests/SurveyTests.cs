using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class SurveyTests(ApiFactory factory) : TestBase(factory)
{
    [Theory]
    [InlineData(SurveyType.TeachingEvaluation)]
    [InlineData(SurveyType.ServiceSurvey)]
    public async Task Full_survey_flow_for_both_types(SurveyType type)
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var other = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id, "Смирнова");
        var student = await StudentAsync(account);
        var classmate = await StudentAsync(await CreateStudentAsync(manager, group.Id, "Кузнецов"));
        var outsider = await StudentAsync(await CreateStudentAsync(manager, other.Id, "Попов"));

        var survey = await CreateSurveyAsync(manager, group.Id, type);

        // Drafts are invisible to students.
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/surveys/{survey.Id}")).StatusCode);

        var opened = await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");
        Assert.Equal(SurveyStatus.Open, opened.Status);

        Assert.Contains(await student.GetJsonAsync<List<NotificationDto>>("/api/notifications"),
            n => n.Type == NotificationType.SurveyAssigned);
        Assert.Empty(await outsider.GetJsonAsync<List<NotificationDto>>("/api/notifications"));

        var detail = await student.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.True(detail.CanRespond);

        var submit = await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", ValidAnswers(detail));
        await submit.EnsureStatusAsync(HttpStatusCode.Created);

        // Manager sees the author and the answers (surveys are not anonymous).
        var responses = await manager.GetJsonAsync<List<SurveyResponseDto>>($"/api/manager/surveys/{survey.Id}/responses");
        var response = Assert.Single(responses);
        Assert.Equal(account.Id, response.StudentId);
        Assert.StartsWith("Смирнова", response.StudentName);
        Assert.Contains(response.Answers, a => a.TextValue == "Всё отлично");

        // Classmate sees the survey but not the other student's answers.
        var classmateView = await classmate.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.False(classmateView.Submitted);
        Assert.Empty(classmateView.MyAnswers);

        // Student of another group cannot see or answer the survey.
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/student/surveys/{survey.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.PostAsync($"/api/student/surveys/{survey.Id}/responses", ValidAnswers(detail))).StatusCode);
        Assert.DoesNotContain(await outsider.GetJsonAsync<List<StudentSurveyListItemDto>>("/api/student/surveys"),
            s => s.Id == survey.Id);

        // Students have no access to the manager's response list.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await classmate.GetAsync($"/api/manager/surveys/{survey.Id}/responses")).StatusCode);

        var closed = await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/close");
        Assert.Equal(SurveyStatus.Closed, closed.Status);
        var late = await classmate.PostAsync($"/api/student/surveys/{survey.Id}/responses", ValidAnswers(detail));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("survey_not_accepting", await late.ErrorCodeAsync());
    }

    [Fact]
    public async Task Answers_are_validated()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        var survey = await CreateSurveyAsync(manager, group.Id, SurveyType.ServiceSurvey);
        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");
        var q = survey.Questions;

        // Required scale question missing.
        var missing = await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", new
        {
            answers = new object[] { new { questionId = q[1].Id, optionId = q[1].Options[0].Id } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // Scale value out of range.
        var outOfRange = await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", new
        {
            answers = new object[]
            {
                new { questionId = q[0].Id, intValue = 6 },
                new { questionId = q[1].Id, optionId = q[1].Options[0].Id }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        // Option from another question.
        var wrongOption = await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", new
        {
            answers = new object[]
            {
                new { questionId = q[0].Id, intValue = 3 },
                new { questionId = q[1].Id, optionId = Guid.NewGuid() }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongOption.StatusCode);

        // Valid response (optional text omitted), then duplicate submission.
        var ok = await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", new
        {
            answers = new object[]
            {
                new { questionId = q[0].Id, intValue = 5 },
                new { questionId = q[1].Id, optionId = q[1].Options[1].Id }
            }
        });
        await ok.EnsureStatusAsync(HttpStatusCode.Created);
        var again = await student.PostAsync($"/api/student/surveys/{survey.Id}/responses", new
        {
            answers = new object[]
            {
                new { questionId = q[0].Id, intValue = 4 },
                new { questionId = q[1].Id, optionId = q[1].Options[0].Id }
            }
        });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Open_survey_cannot_be_edited_and_empty_survey_cannot_be_opened()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var empty = await manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys",
            new { type = "ServiceSurvey", title = "Пустой", groupId = group.Id }, HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/manager/surveys/{empty.Id}/open")).StatusCode);

        var survey = await CreateSurveyAsync(manager, group.Id, SurveyType.ServiceSurvey);
        await manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");
        var edit = await manager.PutAsync($"/api/manager/surveys/{survey.Id}",
            new { type = "ServiceSurvey", title = "Изменён", groupId = group.Id });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }

    [Fact]
    public async Task Invalid_question_definitions_are_rejected()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var res = await manager.PostAsync("/api/manager/surveys", new
        {
            type = "ServiceSurvey", title = "Плохой", groupId = group.Id,
            questions = new object[] { new { text = "Выбор", type = "SingleChoice", isRequired = true, options = new[] { "Один" } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    private static object ValidAnswers(StudentSurveyDetailDto d) => new
    {
        answers = new object[]
        {
            new { questionId = d.Questions[0].Id, intValue = 4 },
            new { questionId = d.Questions[1].Id, optionId = d.Questions[1].Options[0].Id },
            new { questionId = d.Questions[2].Id, textValue = "Всё отлично" }
        }
    };

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
            questions = new object[]
            {
                new { text = "Оцените по шкале", type = "Scale", isRequired = true, scaleMin = 1, scaleMax = 5 },
                new { text = "Выберите вариант", type = "SingleChoice", isRequired = true, options = new[] { "Да", "Нет" } },
                new { text = "Комментарий", type = "Text", isRequired = false }
            }
        }, HttpStatusCode.Created);
    }
}
