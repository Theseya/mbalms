using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class LessonChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    private static readonly string[] AllowedFields =
        ["group", "discipline", "teacher", "startsAt", "endsAt", "format", "location", "comment", "status"];

    [Fact]
    public async Task Lesson_create_update_delete_are_logged_with_actor_and_allow_listed_fields()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);
        var created = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", new
        {
            groupId = group.Id,
            disciplineId = discipline.Id,
            teacherId = teacher.Id,
            startsAt = "2031-03-10T10:00",
            endsAt = "2031-03-10T11:30",
            format = "Online",
            location = "Ауд. 101",
            comment = "Принести ноутбуки",
            status = "Scheduled"
        }, HttpStatusCode.Created);

        var afterCreate = await LogsFor(manager, created.Id);
        Assert.Single(afterCreate);
        Assert.Equal(ChangeLogAction.Created, afterCreate[0].Action);
        Assert.Equal(ChangeLogEntityType.Lesson, afterCreate[0].EntityType);
        Assert.Equal(ApiFactory.ManagerEmail, afterCreate[0].ActorEmail);
        Assert.True(afterCreate[0].EntityExists);
        Assert.All(afterCreate[0].Changes, c => Assert.Contains(c.Field, AllowedFields));
        Assert.Contains(afterCreate[0].Changes, c => c.Field == "comment" && c.New?.GetString() == "set");
        Assert.DoesNotContain("Принести", JsonSerializer.Serialize(afterCreate[0].Changes));

        var teacher2 = await CreateTeacherAsync(manager);
        var discipline2 = await CreateDisciplineAsync(manager);
        await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{created.Id}", new
        {
            groupId = group.Id,
            disciplineId = discipline2.Id,
            teacherId = teacher2.Id,
            startsAt = "2031-03-10T12:00",
            endsAt = "2031-03-10T13:00",
            format = "Online",
            location = "Zoom",
            comment = "Секретный комментарий занятия",
            status = "Scheduled"
        });

        var afterUpdate = await LogsFor(manager, created.Id);
        Assert.Equal(2, afterUpdate.Count);
        Assert.Equal(ChangeLogAction.Updated, afterUpdate[1].Action);
        Assert.Contains(afterUpdate[1].Changes, c => c.Field == "comment" && c.New?.GetString() == "changed");
        Assert.DoesNotContain("Секретный", JsonSerializer.Serialize(afterUpdate[1].Changes));

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/lessons/{created.Id}")).StatusCode);
        var afterDelete = await LogsFor(manager, created.Id);
        Assert.Equal(3, afterDelete.Count);
        Assert.Equal(ChangeLogAction.Deleted, afterDelete[^1].Action);
        Assert.False(afterDelete[^1].EntityExists);
        Assert.False(string.IsNullOrWhiteSpace(afterDelete[^1].EntityTitle));
        Assert.Contains(afterDelete[^1].GroupNames, n => n == group.Name);
    }

    [Fact]
    public async Task Lesson_no_op_update_does_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var lesson = await CreateLessonAsync(manager, group.Id, "2031-04-01T10:00", "2031-04-01T11:00");
        var before = await CountFor(manager, lesson.Id);

        await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", new
        {
            groupId = lesson.GroupId,
            disciplineId = lesson.DisciplineId,
            teacherId = lesson.TeacherId,
            startsAt = "2031-04-01T10:00",
            endsAt = "2031-04-01T11:00",
            format = lesson.Format,
            location = lesson.Location,
            comment = lesson.Comment,
            status = lesson.Status
        });

        Assert.Equal(before, await CountFor(manager, lesson.Id));
    }

    [Fact]
    public async Task Failed_lesson_create_does_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);
        var before = await TotalCount(manager);

        var res = await manager.PostAsync("/api/manager/lessons", new
        {
            groupId = group.Id,
            disciplineId = discipline.Id,
            teacherId = teacher.Id,
            startsAt = "2031-05-01T12:00",
            endsAt = "2031-05-01T11:00",
            format = "Online",
            location = (string?)null,
            comment = (string?)null,
            status = "Scheduled"
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, await TotalCount(manager));
    }

    [Fact]
    public async Task Two_managers_produce_distinct_lesson_actors()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var lesson = await CreateLessonAsync(manager, group.Id, "2031-06-01T10:00", "2031-06-01T11:00");

        var other = await CreateOtherManagerClientAsync();
        await other.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", new
        {
            groupId = lesson.GroupId,
            disciplineId = lesson.DisciplineId,
            teacherId = lesson.TeacherId,
            startsAt = "2031-06-01T11:00",
            endsAt = "2031-06-01T12:00",
            format = lesson.Format,
            location = lesson.Location,
            comment = lesson.Comment,
            status = lesson.Status
        });

        var logs = await LogsFor(manager, lesson.Id);
        Assert.Contains(logs, e => e.Action == ChangeLogAction.Created && e.ActorEmail == ApiFactory.ManagerEmail);
        Assert.Contains(logs, e => e.Action == ChangeLogAction.Updated && e.ActorEmail != ApiFactory.ManagerEmail);
        Assert.Equal(2, logs.Select(e => e.ActorUserId).Distinct().Count());
    }

    private static async Task<List<ChangeLogListItemDto>> LogsFor(ApiClient manager, Guid entityId)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            "/api/manager/change-log?entityType=Lesson&page=1&pageSize=100");
        return page.Items.Where(i => i.EntityId == entityId).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
    }

    private static async Task<int> CountFor(ApiClient manager, Guid entityId) =>
        (await LogsFor(manager, entityId)).Count;

    private static async Task<int> TotalCount(ApiClient manager)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>("/api/manager/change-log?page=1&pageSize=1");
        return page.Total;
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
}
