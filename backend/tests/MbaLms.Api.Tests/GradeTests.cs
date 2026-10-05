using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class GradeTests(ApiFactory factory) : TestBase(factory)
{
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(1000)]
    public async Task Grade_outside_0_100_is_rejected(int value)
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);

        var res = await manager.PostAsync("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("validation_failed", await res.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Boundary_values_are_accepted(int value)
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);

        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value }, HttpStatusCode.Created);
        Assert.Equal(value, grade.Value);
    }

    [Fact]
    public async Task Updating_grade_out_of_range_is_rejected()
    {
        var (manager, grade, _) = await CreateDraftGradeAsync();
        var res = await manager.PutAsync($"/api/manager/grades/{grade.Id}", new { value = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Student_sees_only_own_published_grades()
    {
        var (manager, grade, account) = await CreateDraftGradeAsync();
        var owner = await StudentAsync(account);
        var classmate = await StudentAsync(await CreateStudentAsync(manager, account.GroupId, "Сидоров"));

        Assert.DoesNotContain(await owner.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"), g => g.Id == grade.Id);

        var edited = await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 77 });
        Assert.Equal(GradeStatus.Draft, edited.Status);
        Assert.DoesNotContain(await owner.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"), g => g.Id == grade.Id);
        Assert.Empty(await owner.GetJsonAsync<List<NotificationDto>>("/api/notifications"));

        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");

        var ownGrades = await owner.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades");
        Assert.Equal(77, Assert.Single(ownGrades, g => g.Id == grade.Id).Value);
        Assert.Empty(await classmate.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"));

        var notes = await owner.GetJsonAsync<List<NotificationDto>>("/api/notifications");
        Assert.Contains(notes, n => n.Type == NotificationType.GradePublished);
        Assert.Empty(await classmate.GetJsonAsync<List<NotificationDto>>("/api/notifications"));
    }

    [Fact]
    public async Task Only_one_grade_per_student_discipline_and_period()
    {
        var (manager, grade, account) = await CreateDraftGradeAsync();
        var duplicate = await manager.PostAsync("/api/manager/grades",
            new { studentId = account.Id, disciplineId = grade.DisciplineId, periodId = grade.PeriodId, value = 50 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var otherPeriod = await CreatePeriodAsync(manager);
        var res = await manager.PostAsync("/api/manager/grades",
            new { studentId = account.Id, disciplineId = grade.DisciplineId, periodId = otherPeriod.Id, value = 50 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Bulk_publish_publishes_drafts()
    {
        var (manager, grade, account) = await CreateDraftGradeAsync();
        await manager.PostJsonAsync<object>("/api/manager/grades/publish", new { ids = new[] { grade.Id } });
        var student = await StudentAsync(account);
        Assert.Contains(await student.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"), g => g.Id == grade.Id);
    }

    [Fact]
    public async Task Notifications_on_first_publication_and_changes_of_published_grade_only()
    {
        var (manager, grade, account) = await CreateDraftGradeAsync();
        var student = await StudentAsync(account);
        async Task<int> CountAsync() =>
            (await student.GetJsonAsync<List<NotificationDto>>("/api/notifications")).Count(n => n.Type == NotificationType.GradePublished);

        await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 70 });
        Assert.Equal(0, await CountAsync());

        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");
        Assert.Equal(1, await CountAsync());

        await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 70 });
        Assert.Equal(1, await CountAsync());

        var changed = await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 72, confirmPublishedChange = true });
        Assert.Equal(GradeStatus.Published, changed.Status);
        Assert.Equal(2, await CountAsync());

        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/unpublish");
        await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 75 });
        Assert.Equal(2, await CountAsync());

        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");
        Assert.Equal(3, await CountAsync());
    }

    [Fact]
    public async Task History_records_every_change_and_survives_draft_deletion()
    {
        var (manager, grade, account) = await CreateDraftGradeAsync();
        var url = $"/api/manager/grades/{grade.Id}";
        await manager.PutJsonAsync<GradeDto>(url, new { value = 70 });
        await manager.PutJsonAsync<GradeDto>(url, new { value = 70 });
        await manager.PostJsonAsync<GradeDto>($"{url}/publish");
        await manager.PutJsonAsync<GradeDto>(url, new { value = 80, confirmPublishedChange = true });
        await manager.PostJsonAsync<GradeDto>($"{url}/unpublish");
        await (await manager.DeleteAsync(url)).EnsureStatusAsync(HttpStatusCode.NoContent);

        var recreated = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = account.Id, disciplineId = grade.DisciplineId, periodId = grade.PeriodId, value = 90 }, HttpStatusCode.Created);

        var history = await manager.GetJsonAsync<List<GradeHistoryDto>>($"/api/manager/grades/{recreated.Id}/history");
        var expected = new (GradeChangeAction Action, int? Old, int? New, GradeStatus? OldStatus, GradeStatus? NewStatus)[]
        {
            (GradeChangeAction.Created, null, 90, null, GradeStatus.Draft),
            (GradeChangeAction.Deleted, 80, null, GradeStatus.Draft, null),
            (GradeChangeAction.Unpublished, 80, 80, GradeStatus.Published, GradeStatus.Draft),
            (GradeChangeAction.Updated, 70, 80, GradeStatus.Published, GradeStatus.Published),
            (GradeChangeAction.Published, 70, 70, GradeStatus.Draft, GradeStatus.Published),
            (GradeChangeAction.Updated, 65, 70, GradeStatus.Draft, GradeStatus.Draft),
            (GradeChangeAction.Created, null, 65, null, GradeStatus.Draft),
        };
        Assert.Equal(expected, history.Select(h => (h.Action, h.OldValue, h.NewValue, h.OldStatus, h.NewStatus)).ToArray());
        Assert.All(history, h => Assert.Equal(ApiFactory.ManagerEmail, h.ChangedBy));
        Assert.Equal(recreated.Id, history[0].GradeId);
        Assert.All(history.Skip(1), h => Assert.Equal(grade.Id, h.GradeId));
    }

    [Fact]
    public async Task Bulk_publish_is_recorded_in_history()
    {
        var (manager, grade, _) = await CreateDraftGradeAsync();
        await manager.PostJsonAsync<object>("/api/manager/grades/publish", new { ids = new[] { grade.Id } });
        var history = await manager.GetJsonAsync<List<GradeHistoryDto>>($"/api/manager/grades/{grade.Id}/history");
        Assert.Equal(GradeChangeAction.Published, history[0].Action);
    }

    [Fact]
    public async Task History_is_available_only_to_manager()
    {
        var (_, grade, account) = await CreateDraftGradeAsync();
        var url = $"/api/manager/grades/{grade.Id}/history";
        Assert.Equal(HttpStatusCode.Forbidden, (await (await StudentAsync(account)).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await AnonymousAsync()).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task History_of_unknown_grade_is_not_found()
    {
        var manager = await ManagerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/manager/grades/{Guid.NewGuid()}/history")).StatusCode);
    }

    private async Task<(ApiClient Manager, GradeDto Grade, StudentAccount Student)> CreateDraftGradeAsync()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value = 65 }, HttpStatusCode.Created);
        Assert.Equal(GradeStatus.Draft, grade.Status);
        return (manager, grade, student);
    }
}
