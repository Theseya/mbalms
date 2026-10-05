using System.Net;
using ClosedXML.Excel;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class SurveyModuleTests(ApiFactory factory) : TestBase(factory)
{
    private sealed record Setup(ApiClient Manager, GroupDto Group, StudentAccount Anna, StudentAccount Boris,
        TeacherDto Teacher, DisciplineDto Discipline);

    private async Task<Setup> SetupAsync()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var boris = await CreateStudentAsync(manager, group.Id, "Борисов");
        var anna = await CreateStudentAsync(manager, group.Id, "Аннина");
        return new Setup(manager, group, anna, boris, await CreateTeacherAsync(manager), await CreateDisciplineAsync(manager));
    }

    private static object[] Questions(string? optionText = null) =>
    [
        new { text = "Насколько понятен материал?", type = "Scale", isRequired = true, scaleMin = 1, scaleMax = 5,
            scaleMinLabel = "Совсем непонятно", scaleMaxLabel = "Полностью понятно" },
        new { text = "Формат занятий", type = "SingleChoice", isRequired = true, options = new[] { optionText ?? "Очно", "Онлайн" } },
        new { text = "Комментарий", type = "Text", isRequired = false }
    ];

    private static object Body(Setup s, SurveyType type, string? opensAt = null, string? closesAt = null, object[]? questions = null) => new
    {
        type = type.ToString(),
        title = Unique(type == SurveyType.TeachingEvaluation ? "Оценка преподавания" : "Сервисный опрос"),
        groupId = s.Group.Id,
        teacherId = s.Teacher.Id,
        disciplineId = s.Discipline.Id,
        opensAt,
        closesAt,
        questions = questions ?? Questions()
    };

    private static Task<SurveyDetailDto> CreateAsync(Setup s, SurveyType type, string? opensAt = null, string? closesAt = null,
        object[]? questions = null) =>
        s.Manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys", Body(s, type, opensAt, closesAt, questions), HttpStatusCode.Created);

    private static Task<SurveyDetailDto> PublishAsync(Setup s, SurveyDetailDto survey) =>
        s.Manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");

    private static object Answers(SurveyQuestionDto[] q, int scale = 4, int option = 0, string? text = "Всё хорошо") => new
    {
        answers = new object[]
        {
            new { questionId = q[0].Id, intValue = scale },
            new { questionId = q[1].Id, optionId = q[1].Options[option].Id },
            new { questionId = q[2].Id, textValue = text }
        }
    };

    private static Task<HttpResponseMessage> SubmitAsync(ApiClient student, SurveyDetailDto survey, object body) =>
        student.PostAsync($"/api/student/surveys/{survey.Id}/responses", body);

    private async Task SetWindowAsync(Guid surveyId, DateTimeOffset? opensAt, DateTimeOffset? closesAt)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Surveys.Where(x => x.Id == surveyId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.OpensAt, opensAt).SetProperty(x => x.ClosesAt, closesAt));
    }

    [Theory]
    [InlineData(SurveyType.TeachingEvaluation)]
    [InlineData(SurveyType.ServiceSurvey)]
    public async Task Both_survey_types_go_through_draft_publish_answer_close(SurveyType type)
    {
        var s = await SetupAsync();
        var survey = await CreateAsync(s, type);
        Assert.Equal(SurveyStatus.Draft, survey.Status);
        Assert.Equal(type, survey.Type);
        if (type == SurveyType.TeachingEvaluation)
            Assert.Equal((s.Teacher.Id, s.Discipline.Id), (survey.TeacherId!.Value, survey.DisciplineId!.Value));
        else
            Assert.True(survey.TeacherId is null && survey.DisciplineId is null);

        // A draft can be edited: questions are replaced.
        var edited = await s.Manager.PutJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}",
            Body(s, type, questions: Questions("Гибрид")));
        Assert.Equal("Гибрид", edited.Questions[1].Options[0].Text);
        Assert.Equal(("Совсем непонятно", "Полностью понятно"), (edited.Questions[0].ScaleMinLabel, edited.Questions[0].ScaleMaxLabel));

        var anna = await StudentAsync(s.Anna);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"/api/student/surveys/{survey.Id}")).StatusCode);

        var published = await PublishAsync(s, edited);
        Assert.Equal(SurveyStatus.Open, published.Status);
        Assert.NotNull(published.PublishedAt);
        Assert.Equal((0, 2), (published.ResponseCount, published.StudentCount));

        var view = await anna.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.True(view.CanRespond);
        Assert.Equal("Полностью понятно", view.Questions[0].ScaleMaxLabel);
        await (await SubmitAsync(anna, survey, Answers([.. view.Questions]))).EnsureStatusAsync(HttpStatusCode.Created);

        var closed = await s.Manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/close");
        Assert.Equal((SurveyStatus.Closed, 1), (closed.Status, closed.ResponseCount));
        var late = await SubmitAsync(await StudentAsync(s.Boris), survey, Answers([.. view.Questions]));
        Assert.Equal("survey_not_accepting", await late.ErrorCodeAsync());
        Assert.Equal("survey_not_editable",
            await (await s.Manager.PutAsync($"/api/manager/surveys/{survey.Id}", Body(s, type))).ErrorCodeAsync());
    }

    [Fact]
    public async Task Teaching_evaluation_requires_teacher_and_discipline()
    {
        var s = await SetupAsync();
        var res = await s.Manager.PostAsync("/api/manager/surveys",
            new { type = "TeachingEvaluation", title = "Без преподавателя", groupId = s.Group.Id, disciplineId = s.Discipline.Id });
        Assert.Equal("required", (await res.FieldErrorsAsync())["teacherId"]);
        res = await s.Manager.PostAsync("/api/manager/surveys",
            new { type = "TeachingEvaluation", title = "Без дисциплины", groupId = s.Group.Id, teacherId = s.Teacher.Id });
        Assert.Equal("required", (await res.FieldErrorsAsync())["disciplineId"]);
        res = await s.Manager.PostAsync("/api/manager/surveys",
            new { type = "TeachingEvaluation", title = "Чужой", groupId = s.Group.Id, teacherId = Guid.NewGuid(), disciplineId = s.Discipline.Id });
        Assert.Equal("not_found", (await res.FieldErrorsAsync())["teacherId"]);

        var service = await s.Manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys",
            new { type = "ServiceSurvey", title = "Столовая", groupId = s.Group.Id }, HttpStatusCode.Created);
        Assert.Null(service.TeacherId);
    }

    [Theory]
    [InlineData(3, 1, "scaleMax", "range")]
    [InlineData(5, 5, "scaleMax", "range")]
    [InlineData(-101, 5, "scaleMax", "range")]
    [InlineData(1, 101, "scaleMax", "range")]
    public async Task Scale_bounds_are_validated(int min, int max, string field, string code)
    {
        var s = await SetupAsync();
        var res = await s.Manager.PostAsync("/api/manager/surveys", Body(s, SurveyType.ServiceSurvey, questions:
            [new { text = "Шкала", type = "Scale", isRequired = true, scaleMin = min, scaleMax = max }]));
        Assert.Equal(code, (await res.FieldErrorsAsync())[$"questions[0].{field}"]);
    }

    [Fact]
    public async Task Scale_labels_are_limited_in_length()
    {
        var s = await SetupAsync();
        var res = await s.Manager.PostAsync("/api/manager/surveys", Body(s, SurveyType.ServiceSurvey, questions:
            [new { text = "Шкала", type = "Scale", isRequired = true, scaleMin = 1, scaleMax = 5, scaleMinLabel = new string('x', 101) }]));
        Assert.Equal("max_length", (await res.FieldErrorsAsync())["questions[0].scaleMinLabel"]);
    }

    [Fact]
    public async Task Publication_notifies_only_the_target_group_and_only_once()
    {
        var s = await SetupAsync();
        var outsider = await StudentAsync(await CreateStudentAsync(s.Manager, (await CreateGroupAsync(s.Manager)).Id));
        var anna = await StudentAsync(s.Anna);
        var boris = await StudentAsync(s.Boris);
        async Task<int> CountAsync(ApiClient c) =>
            (await c.GetJsonAsync<List<NotificationDto>>("/api/notifications")).Count(n => n.Type == NotificationType.SurveyAssigned);

        var survey = await CreateAsync(s, SurveyType.TeachingEvaluation);
        Assert.Equal(0, await CountAsync(anna));

        await PublishAsync(s, survey);
        Assert.Equal((1, 1, 0), (await CountAsync(anna), await CountAsync(boris), await CountAsync(outsider)));
        var note = (await anna.GetJsonAsync<List<NotificationDto>>("/api/notifications")).Single(n => n.Type == NotificationType.SurveyAssigned);
        Assert.Equal(survey.Id, note.Payload.GetProperty("surveyId").GetGuid());

        await s.Manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/close");
        await PublishAsync(s, survey);
        Assert.Equal(1, await CountAsync(anna));
    }

    [Fact]
    public async Task Responses_are_accepted_only_inside_the_availability_window()
    {
        var s = await SetupAsync();
        var anna = await StudentAsync(s.Anna);
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.ServiceSurvey, opensAt: "2099-01-01T09:00", closesAt: "2099-01-31T18:00"));
        var answers = Answers([.. survey.Questions]);

        // Before the window opens.
        var view = await anna.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.False(view.CanRespond);
        Assert.Equal(new DateTime(2099, 1, 1, 9, 0, 0), view.OpensAtLocal);
        Assert.Equal(0, (await anna.GetJsonAsync<DashboardDto>("/api/student/dashboard")).PendingSurveys);
        Assert.Equal("survey_not_accepting", await (await SubmitAsync(anna, survey, answers)).ErrorCodeAsync());

        // Inside the window.
        await SetWindowAsync(survey.Id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(1, (await anna.GetJsonAsync<DashboardDto>("/api/student/dashboard")).PendingSurveys);
        var boris = await StudentAsync(s.Boris);
        await (await SubmitAsync(boris, survey, answers)).EnsureStatusAsync(HttpStatusCode.Created);

        // After the window has ended, even though the survey is still open.
        await SetWindowAsync(survey.Id, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False((await anna.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}")).CanRespond);
        Assert.Equal("survey_not_accepting", await (await SubmitAsync(anna, survey, answers)).ErrorCodeAsync());
    }

    [Fact]
    public async Task Survey_whose_window_has_ended_cannot_be_published()
    {
        var s = await SetupAsync();
        var survey = await CreateAsync(s, SurveyType.ServiceSurvey, closesAt: "2020-01-01T10:00");
        var res = await s.Manager.PostAsync($"/api/manager/surveys/{survey.Id}/open");
        Assert.Equal("survey_window_ended", await res.ErrorCodeAsync());
        Assert.Empty((await (await StudentAsync(s.Anna)).GetJsonAsync<List<NotificationDto>>("/api/notifications")));
    }

    [Fact]
    public async Task Window_must_end_after_it_starts()
    {
        var s = await SetupAsync();
        var res = await s.Manager.PostAsync("/api/manager/surveys",
            Body(s, SurveyType.ServiceSurvey, opensAt: "2030-01-10T10:00", closesAt: "2030-01-10T10:00"));
        Assert.Equal("end_before_start", (await res.FieldErrorsAsync())["closesAt"]);
    }

    [Fact]
    public async Task Answers_are_validated_against_the_survey_questions()
    {
        var s = await SetupAsync();
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.TeachingEvaluation));
        var other = await PublishAsync(s, await CreateAsync(s, SurveyType.ServiceSurvey));
        var q = survey.Questions.ToArray();
        var anna = await StudentAsync(s.Anna);

        async Task<FieldErrors> ErrorsAsync(params object[] answers)
        {
            var res = await SubmitAsync(anna, survey, new { answers });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.FieldErrorsAsync();
        }
        object Scale(int v) => new { questionId = q[0].Id, intValue = v };
        object Choice(Guid id) => new { questionId = q[1].Id, optionId = id };
        var yes = q[1].Options[0].Id;
        string Key(int i) => q[i].Id.ToString();

        Assert.Equal("required", (await ErrorsAsync(Choice(yes)))[Key(0)]);
        Assert.Equal("required", (await ErrorsAsync(Scale(3)))[Key(1)]);
        Assert.Equal("required", (await ErrorsAsync(Scale(3), new { questionId = q[1].Id }))[Key(1)]);
        Assert.Equal("range", (await ErrorsAsync(Scale(0), Choice(yes)))[Key(0)]);
        Assert.Equal("range", (await ErrorsAsync(Scale(6), Choice(yes)))[Key(0)]);
        // Option of another survey's question.
        Assert.Equal("invalid", (await ErrorsAsync(Scale(3), Choice(other.Questions[1].Options[0].Id)))[Key(1)]);
        // Value of the wrong kind for the question type.
        Assert.Equal("invalid", (await ErrorsAsync(new { questionId = q[0].Id, textValue = "5" }, Choice(yes)))[Key(0)]);
        Assert.Equal("invalid", (await ErrorsAsync(Scale(3), Choice(yes), new { questionId = q[2].Id, intValue = 1 }))[Key(2)]);
        Assert.Equal("max_length",
            (await ErrorsAsync(Scale(3), Choice(yes), new { questionId = q[2].Id, textValue = new string('a', 4001) }))["answers[2].textValue"]);
        // Question of another survey, unknown question and the same question twice.
        Assert.Equal("invalid", (await ErrorsAsync(Scale(3), Choice(yes), new { questionId = other.Questions[2].Id, textValue = "x" }))["answers"]);
        Assert.Equal("invalid", (await ErrorsAsync(Scale(3), Choice(yes), new { questionId = Guid.NewGuid(), textValue = "x" }))["answers"]);
        Assert.Equal("invalid", (await ErrorsAsync(Scale(3), Scale(4), Choice(yes)))["answers"]);

        Assert.Equal(0, (await s.Manager.GetJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}")).ResponseCount);

        // Boundaries of the scale are accepted; the optional text question may be skipped.
        await (await SubmitAsync(anna, survey, new { answers = new[] { Scale(1), Choice(yes) } })).EnsureStatusAsync(HttpStatusCode.Created);
        await (await SubmitAsync(await StudentAsync(s.Boris), survey, Answers(q, scale: 5))).EnsureStatusAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_student_can_answer_a_survey_only_once()
    {
        var s = await SetupAsync();
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.ServiceSurvey));
        var q = survey.Questions.ToArray();
        var anna = await StudentAsync(s.Anna);

        await (await SubmitAsync(anna, survey, Answers(q, scale: 2, text: "Первый ответ"))).EnsureStatusAsync(HttpStatusCode.Created);
        var again = await SubmitAsync(anna, survey, Answers(q, scale: 5, text: "Второй ответ"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("survey_already_submitted", await again.ErrorCodeAsync());

        var view = await anna.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.True(view.Submitted);
        Assert.False(view.CanRespond);
        var response = Assert.Single(await s.Manager.GetJsonAsync<List<SurveyResponseDto>>($"/api/manager/surveys/{survey.Id}/responses"));
        Assert.Contains(response.Answers, a => a.TextValue == "Первый ответ");
        Assert.Contains(response.Answers, a => a.IntValue == 2);
    }

    [Fact]
    public async Task Parallel_submissions_store_exactly_one_response()
    {
        var s = await SetupAsync();
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.ServiceSurvey));
        var anna = await StudentAsync(s.Anna);
        var body = Answers([.. survey.Questions]);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SubmitAsync(anna, survey, body)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var r in results.Where(r => r.StatusCode != HttpStatusCode.Created))
            Assert.Equal("survey_already_submitted", await r.ErrorCodeAsync());
        Assert.Single(await s.Manager.GetJsonAsync<List<SurveyResponseDto>>($"/api/manager/surveys/{survey.Id}/responses"));
    }

    [Fact]
    public async Task Only_students_of_the_target_group_can_see_and_answer()
    {
        var s = await SetupAsync();
        var otherGroup = await CreateGroupAsync(s.Manager);
        var outsider = await StudentAsync(await CreateStudentAsync(s.Manager, otherGroup.Id));
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.TeachingEvaluation));
        var body = Answers([.. survey.Questions]);

        Assert.DoesNotContain(await outsider.GetJsonAsync<List<StudentSurveyListItemDto>>("/api/student/surveys"), x => x.Id == survey.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/student/surveys/{survey.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(outsider, survey, body)).StatusCode);

        // A student moved to another group loses access to the old group's survey.
        var boris = await StudentAsync(s.Boris);
        await s.Manager.PutJsonAsync<StudentDto>($"/api/manager/students/{s.Boris.Id}",
            new { lastName = "Борисов", firstName = "Иван", email = s.Boris.Email, groupId = otherGroup.Id });
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(boris, survey, body)).StatusCode);

        // Students of an archived group keep read access but cannot answer.
        await s.Manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{s.Group.Id}/archive");
        var anna = await StudentAsync(s.Anna);
        Assert.False((await anna.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}")).CanRespond);
        Assert.Equal("survey_not_accepting", await (await SubmitAsync(anna, survey, body)).ErrorCodeAsync());
        Assert.Empty(await s.Manager.GetJsonAsync<List<SurveyResponseDto>>($"/api/manager/surveys/{survey.Id}/responses"));
    }

    [Fact]
    public async Task Manager_sees_authors_students_see_only_their_own_status()
    {
        var s = await SetupAsync();
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.TeachingEvaluation));
        var q = survey.Questions.ToArray();
        var anna = await StudentAsync(s.Anna);
        var boris = await StudentAsync(s.Boris);
        await (await SubmitAsync(anna, survey, Answers(q, scale: 5, option: 1, text: "Секрет Анны"))).EnsureStatusAsync(HttpStatusCode.Created);

        var response = Assert.Single(await s.Manager.GetJsonAsync<List<SurveyResponseDto>>($"/api/manager/surveys/{survey.Id}/responses"));
        Assert.Equal((s.Anna.Id, "Аннина Иван", s.Group.Name), (response.StudentId, response.StudentName, response.GroupName));
        Assert.Equal(5, response.Answers.Single(a => a.QuestionId == q[0].Id).IntValue);
        Assert.Equal("Онлайн", response.Answers.Single(a => a.QuestionId == q[1].Id).OptionText);
        Assert.Equal("Секрет Анны", response.Answers.Single(a => a.QuestionId == q[2].Id).TextValue);

        var borisView = await boris.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.False(borisView.Submitted);
        Assert.Empty(borisView.MyAnswers);
        var borisList = await boris.GetJsonAsync<List<StudentSurveyListItemDto>>("/api/student/surveys");
        Assert.False(borisList.Single(x => x.Id == survey.Id).Submitted);
        foreach (var url in new[] { $"/api/student/surveys/{survey.Id}", "/api/student/surveys", "/api/student/dashboard" })
        {
            var raw = await (await boris.GetAsync(url)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("Секрет Анны", raw);
            Assert.DoesNotContain("Аннина", raw);
            Assert.DoesNotContain("responseCount", raw, StringComparison.OrdinalIgnoreCase);
        }

        var annaView = await anna.GetJsonAsync<StudentSurveyDetailDto>($"/api/student/surveys/{survey.Id}");
        Assert.True(annaView.Submitted);
        Assert.Equal(3, annaView.MyAnswers.Count);
    }

    [Fact]
    public async Task Survey_management_and_results_are_manager_only()
    {
        var s = await SetupAsync();
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.ServiceSurvey));
        var student = await StudentAsync(s.Anna);
        var anonymous = await AnonymousAsync();
        var body = Body(s, SurveyType.ServiceSurvey);

        foreach (var (client, expected) in new[] { (student, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(expected, (await client.GetAsync("/api/manager/surveys")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/manager/surveys/{survey.Id}")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/manager/surveys", body)).StatusCode);
            Assert.Equal(expected, (await client.PutAsync($"/api/manager/surveys/{survey.Id}", body)).StatusCode);
            Assert.Equal(expected, (await client.PostAsync($"/api/manager/surveys/{survey.Id}/close")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync($"/api/manager/surveys/{survey.Id}/open")).StatusCode);
            Assert.Equal(expected, (await client.DeleteAsync($"/api/manager/surveys/{survey.Id}")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/manager/surveys/{survey.Id}/responses")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/manager/exports/surveys/{survey.Id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/student/surveys")).StatusCode);
        // The manager is not a student: the student cabinet is closed to them.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.GetAsync("/api/student/surveys")).StatusCode);
        Assert.Equal(SurveyStatus.Open, (await s.Manager.GetJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}")).Status);
    }

    [Fact]
    public async Task Results_export_contains_authors_and_answers_and_keeps_formulas_as_text()
    {
        var s = await SetupAsync();
        const string formulaOption = "+SUM(1,2)";
        const string formulaText = "=HYPERLINK(\"http://evil\",\"x\")";
        var survey = await PublishAsync(s, await CreateAsync(s, SurveyType.TeachingEvaluation, questions: Questions(formulaOption)));
        var q = survey.Questions.ToArray();
        await (await SubmitAsync(await StudentAsync(s.Anna), survey, Answers(q, scale: 3, option: 0, text: formulaText)))
            .EnsureStatusAsync(HttpStatusCode.Created);
        await (await SubmitAsync(await StudentAsync(s.Boris), survey, Answers(q, scale: 5, option: 1, text: null)))
            .EnsureStatusAsync(HttpStatusCode.Created);

        var res = await s.Manager.GetAsync($"/api/manager/exports/surveys/{survey.Id}");
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType?.MediaType);
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(1);
        Assert.Equal(["Студент", "Группа", "Отправлено", "1. Насколько понятен материал?", "2. Формат занятий", "3. Комментарий"],
            ws.Row(1).CellsUsed().Select(c => c.GetString()));

        Assert.Equal("Аннина Иван", ws.Cell(2, 1).GetString());
        Assert.Equal(s.Group.Name, ws.Cell(2, 2).GetString());
        Assert.Equal(XLDataType.DateTime, ws.Cell(2, 3).DataType);
        Assert.Equal(3, ws.Cell(2, 4).GetValue<int>());
        Assert.Equal(XLDataType.Number, ws.Cell(2, 4).DataType);
        foreach (var (cell, expected) in new[] { (ws.Cell(2, 5), formulaOption), (ws.Cell(2, 6), formulaText) })
        {
            Assert.False(cell.HasFormula);
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.True(cell.Style.IncludeQuotePrefix);
            Assert.Equal(expected, cell.GetString());
        }
        Assert.Equal(("Борисов Иван", "Онлайн"), (ws.Cell(3, 1).GetString(), ws.Cell(3, 5).GetString()));
        Assert.True(ws.Cell(3, 6).IsEmpty());

        using var en = new XLWorkbook(await (await s.Manager.GetAsync($"/api/manager/exports/surveys/{survey.Id}?lang=en")).Content.ReadAsStreamAsync());
        Assert.Equal(["Student", "Group", "Submitted at"], en.Worksheet(1).Row(1).CellsUsed().Take(3).Select(c => c.GetString()));
    }
}
