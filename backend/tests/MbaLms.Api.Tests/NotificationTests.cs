using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class NotificationTests(ApiFactory factory) : TestBase(factory)
{
    private sealed record Setup(ApiClient Manager, GroupDto Group, StudentAccount Anna, StudentAccount Boris, StudentAccount Outsider)
    {
        public Guid OtherGroupId => Outsider.GroupId;
    }

    private async Task<Setup> SetupAsync()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var other = await CreateGroupAsync(manager);
        return new Setup(manager, group, await CreateStudentAsync(manager, group.Id, "Аннина"),
            await CreateStudentAsync(manager, group.Id, "Борисов"), await CreateStudentAsync(manager, other.Id, "Чужой"));
    }

    private static Task<List<NotificationDto>> ListAsync(ApiClient client, string query = "") =>
        client.GetJsonAsync<List<NotificationDto>>($"/api/notifications{query}");

    private static object LessonBody(LessonDto l, string? location = null, string status = "Scheduled") => new
    {
        groupId = l.GroupId, disciplineId = l.DisciplineId, teacherId = l.TeacherId,
        startsAt = l.StartsAtLocal.ToString("yyyy-MM-ddTHH:mm"), endsAt = l.EndsAtLocal.ToString("yyyy-MM-ddTHH:mm"),
        format = l.Format?.ToString(), location = location ?? l.Location, comment = l.Comment, status
    };

    private static void AssertFresh(NotificationDto n)
    {
        Assert.Null(n.ReadAt);
        Assert.InRange(n.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Publishing_a_survey_notifies_each_student_of_the_group()
    {
        var s = await SetupAsync();
        var survey = await s.Manager.PostJsonAsync<SurveyDetailDto>("/api/manager/surveys", new
        {
            type = "ServiceSurvey", title = "Библиотека", groupId = s.Group.Id,
            questions = new object[] { new { text = "Комментарий", type = "Text", isRequired = false } }
        }, HttpStatusCode.Created);
        await s.Manager.PostJsonAsync<SurveyDetailDto>($"/api/manager/surveys/{survey.Id}/open");

        foreach (var account in new[] { s.Anna, s.Boris })
        {
            var n = Assert.Single(await ListAsync(await StudentAsync(account)));
            Assert.Equal(NotificationType.SurveyAssigned, n.Type);
            Assert.Equal(survey.Id, n.Payload.GetProperty("surveyId").GetGuid());
            Assert.Equal("Библиотека", n.Payload.GetProperty("title").GetString());
            Assert.Equal("ServiceSurvey", n.Payload.GetProperty("surveyType").GetString());
            AssertFresh(n);
        }
        Assert.Empty(await ListAsync(await StudentAsync(s.Outsider)));
        Assert.Empty(await ListAsync(s.Manager));
    }

    [Fact]
    public async Task Schedule_changes_notify_the_group_only_when_something_changed()
    {
        var s = await SetupAsync();
        var lesson = await CreateLessonAsync(s.Manager, s.Group.Id, "2031-05-10T10:00", "2031-05-10T11:30");
        var anna = await StudentAsync(s.Anna);
        async Task<string[]> ChangesAsync() => (await ListAsync(anna))
            .Where(n => n.Type == NotificationType.ScheduleChanged).Select(n => n.Payload.GetProperty("change").GetString()!).ToArray();

        var created = Assert.Single(await ListAsync(anna));
        Assert.Equal(lesson.Id, created.Payload.GetProperty("lessonId").GetGuid());
        Assert.Equal(lesson.DisciplineName, created.Payload.GetProperty("disciplineName").GetString());
        Assert.Equal("2031-05-10T10:00:00", created.Payload.GetProperty("startsAtLocal").GetString());
        AssertFresh(created);

        // Saving the unchanged form does not notify.
        await s.Manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", LessonBody(lesson));
        Assert.Equal(["created"], await ChangesAsync());

        await s.Manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", LessonBody(lesson, location: "Ауд. 101"));
        await s.Manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", LessonBody(lesson, location: "Ауд. 101", status: "Cancelled"));
        await (await s.Manager.DeleteAsync($"/api/manager/lessons/{lesson.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(["deleted", "cancelled", "updated", "created"], await ChangesAsync());
        Assert.Equal(4, (await ListAsync(await StudentAsync(s.Boris))).Count);
        Assert.Empty(await ListAsync(await StudentAsync(s.Outsider)));
    }

    [Fact]
    public async Task Moving_a_lesson_to_another_group_notifies_both_groups()
    {
        var s = await SetupAsync();
        var lesson = await CreateLessonAsync(s.Manager, s.Group.Id);
        await s.Manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", LessonBody(lesson with { GroupId = s.OtherGroupId }));

        Assert.Equal(["deleted", "created"], (await ListAsync(await StudentAsync(s.Anna))).Select(n => n.Payload.GetProperty("change").GetString()!));
        Assert.Equal(["updated"], (await ListAsync(await StudentAsync(s.Outsider))).Select(n => n.Payload.GetProperty("change").GetString()!));
    }

    [Fact]
    public async Task Grade_notifications_go_only_to_the_graded_student()
    {
        var s = await SetupAsync();
        var discipline = await CreateDisciplineAsync(s.Manager);
        var period = await CreatePeriodAsync(s.Manager);
        var grade = await s.Manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = s.Anna.Id, disciplineId = discipline.Id, periodId = period.Id, value = 70 }, HttpStatusCode.Created);
        var anna = await StudentAsync(s.Anna);
        Assert.Empty(await ListAsync(anna));

        await s.Manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");
        await s.Manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 75, confirmPublishedChange = true });

        var notes = await ListAsync(anna);
        Assert.Equal(["updated", "published"], notes.Select(n => n.Payload.GetProperty("change").GetString()!));
        Assert.All(notes, n =>
        {
            Assert.Equal(NotificationType.GradePublished, n.Type);
            Assert.Equal(grade.Id, n.Payload.GetProperty("gradeId").GetGuid());
            Assert.Equal(discipline.Name, n.Payload.GetProperty("disciplineName").GetString());
            Assert.Equal(period.Name, n.Payload.GetProperty("periodName").GetString());
            Assert.False(n.Payload.TryGetProperty("value", out _));
            AssertFresh(n);
        });
        Assert.Empty(await ListAsync(await StudentAsync(s.Boris)));
    }

    [Fact]
    public async Task Users_read_and_mark_only_their_own_notifications()
    {
        var s = await SetupAsync();
        await CreateLessonAsync(s.Manager, s.Group.Id, "2031-05-10T10:00", "2031-05-10T11:30");
        await CreateLessonAsync(s.Manager, s.Group.Id, "2031-05-11T10:00", "2031-05-11T11:30");
        var anna = await StudentAsync(s.Anna);
        var boris = await StudentAsync(s.Boris);
        var annaNotes = await ListAsync(anna);
        var borisNotes = await ListAsync(boris);
        Assert.Equal(2, annaNotes.Count);
        Assert.Empty(annaNotes.Select(n => n.Id).Intersect(borisNotes.Select(n => n.Id)));
        Assert.True(annaNotes[0].CreatedAt >= annaNotes[1].CreatedAt);

        async Task<int> UnreadAsync(ApiClient c) =>
            (await c.GetJsonAsync<System.Text.Json.JsonElement>("/api/notifications/unread-count")).GetProperty("count").GetInt32();
        Assert.Equal(2, await UnreadAsync(anna));

        // Someone else's notification looks like it does not exist, for a classmate and for the manager.
        foreach (var other in new[] { boris, s.Manager })
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/notifications/{annaNotes[0].Id}/read")).StatusCode);
        await (await boris.PostAsync("/api/notifications/read-all")).EnsureStatusAsync(HttpStatusCode.NoContent);
        await (await s.Manager.PostAsync("/api/notifications/read-all")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(2, await UnreadAsync(anna));
        Assert.All(await ListAsync(anna), n => Assert.Null(n.ReadAt));

        await (await anna.PostAsync($"/api/notifications/{annaNotes[0].Id}/read")).EnsureStatusAsync(HttpStatusCode.NoContent);
        var readAt = (await ListAsync(anna)).Single(n => n.Id == annaNotes[0].Id).ReadAt;
        Assert.NotNull(readAt);
        await (await anna.PostAsync($"/api/notifications/{annaNotes[0].Id}/read")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(readAt, (await ListAsync(anna)).Single(n => n.Id == annaNotes[0].Id).ReadAt);
        Assert.Equal(1, await UnreadAsync(anna));
        Assert.Equal(annaNotes[1].Id, Assert.Single(await ListAsync(anna, "?unreadOnly=true")).Id);
        Assert.Single(await ListAsync(anna, "?take=1"));

        await (await anna.PostAsync("/api/notifications/read-all")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(0, await UnreadAsync(anna));
    }

    [Fact]
    public async Task Anonymous_users_have_no_access_to_notifications()
    {
        var anonymous = await AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notifications/read-all")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/notifications/{Guid.NewGuid()}/read")).StatusCode);
    }
}
