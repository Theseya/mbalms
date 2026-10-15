using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class ChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Announcement_actions_are_logged_with_actor_and_allowed_fields_only()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var group2 = await CreateGroupAsync(manager);

        var created = await CreateAnnouncementAsync(manager, [group.Id], title: "Журнал A", body: "Секретный текст\nстрока 2");
        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{created.Id}/publish");
        await manager.PutJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{created.Id}", new
        {
            title = "Журнал A2",
            body = "Другой секретный текст",
            isPinned = true,
            expiresAt = (string?)null,
            groupIds = new[] { group.Id, group2.Id }
        });
        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{created.Id}/unpublish");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/announcements/{created.Id}")).StatusCode);

        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            $"/api/manager/change-log?entityType=Announcement&page=1&pageSize=50");
        var mine = page.Items.Where(i => i.EntityId == created.Id).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
        Assert.Equal(5, mine.Count);
        Assert.Equal(
            [ChangeLogAction.Created, ChangeLogAction.Published, ChangeLogAction.Updated, ChangeLogAction.Unpublished, ChangeLogAction.Deleted],
            mine.Select(x => x.Action).ToArray());
        Assert.All(mine, e =>
        {
            Assert.Equal(ApiFactory.ManagerEmail, e.ActorEmail);
            Assert.False(e.ActorIsSystem);
            Assert.False(e.ActorMissing);
            Assert.NotNull(e.ActorUserId);
            Assert.DoesNotContain("Секретный", JsonSerializer.Serialize(e.Changes));
            Assert.DoesNotContain("password", JsonSerializer.Serialize(e.Changes), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", JsonSerializer.Serialize(e.Changes), StringComparison.OrdinalIgnoreCase);
            Assert.All(e.Changes, c => Assert.Contains(c.Field, new[] { "title", "body", "groupIds", "isPinned", "expiresAt", "status" }));
        });

        var createdEntry = mine[0];
        Assert.Contains(createdEntry.Changes, c => c.Field == "body" && c.New?.GetString() == "set");
        var updated = mine[2];
        Assert.Contains(updated.Changes, c => c.Field == "body" && c.New?.GetString() == "changed");
        Assert.Contains(updated.Changes, c => c.Field == "title");
        Assert.False(mine[^1].EntityExists);
        Assert.Equal("Журнал A2", mine[^1].EntityTitle);
    }

    [Fact]
    public async Task No_op_update_and_idempotent_publish_unpublish_do_not_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var a = await CreateAnnouncementAsync(manager, [group.Id], title: "NoOp");
        var before = await CountForEntity(manager, a.Id);

        await manager.PutJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}", new
        {
            title = a.Title, body = a.Body, isPinned = a.IsPinned, expiresAt = (string?)null, groupIds = new[] { group.Id }
        });
        Assert.Equal(before, await CountForEntity(manager, a.Id));

        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        var afterPublish = await CountForEntity(manager, a.Id);
        Assert.Equal(before + 1, afterPublish);

        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        Assert.Equal(afterPublish, await CountForEntity(manager, a.Id));

        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/unpublish");
        var afterUnpublish = await CountForEntity(manager, a.Id);
        Assert.Equal(afterPublish + 1, afterUnpublish);

        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/unpublish");
        Assert.Equal(afterUnpublish, await CountForEntity(manager, a.Id));
    }

    [Fact]
    public async Task Failed_validation_does_not_create_log_or_entity()
    {
        var manager = await ManagerAsync();
        var before = await TotalCount(manager);

        var res = await manager.PostAsync("/api/manager/announcements", new
        {
            title = "", body = "x", isPinned = false, expiresAt = (string?)null, groupIds = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, await TotalCount(manager));
    }

    [Fact]
    public async Task Failed_publish_does_not_log()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 5, 1, 12, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        // expiresAt wall-clock MSK → past relative to clock
        var a = await CreateAnnouncementAsync(manager, [group.Id], expiresAt: "2030-05-01T12:00");
        var before = await CountForEntity(manager, a.Id);

        var res = await manager.PostAsync($"/api/manager/announcements/{a.Id}/publish");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, await CountForEntity(manager, a.Id));
        Assert.Equal(AnnouncementStatus.Draft, (await manager.GetJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}")).Status);
        Factory.Clock.ResetToSystem();
    }

    [Fact]
    public async Task Two_managers_produce_distinct_actors()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var a = await CreateAnnouncementAsync(manager, [group.Id], title: "Shared");

        var other = await CreateOtherManagerClientAsync();
        await other.PutJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}", new
        {
            title = "Shared by other",
            body = a.Body,
            isPinned = false,
            expiresAt = (string?)null,
            groupIds = new[] { group.Id }
        });

        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            $"/api/manager/change-log?entityType=Announcement&page=1&pageSize=20");
        var mine = page.Items.Where(i => i.EntityId == a.Id).ToList();
        Assert.Contains(mine, e => e.Action == ChangeLogAction.Created && e.ActorEmail == ApiFactory.ManagerEmail);
        Assert.Contains(mine, e => e.Action == ChangeLogAction.Updated && e.ActorEmail != ApiFactory.ManagerEmail);
        Assert.Equal(2, mine.Select(e => e.ActorUserId).Distinct().Count());
    }

    [Fact]
    public async Task Filters_and_pagination_work()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        for (var i = 0; i < 3; i++)
            await CreateAnnouncementAsync(manager, [group.Id], title: $"Page{i}");

        var page1 = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            "/api/manager/change-log?action=Created&page=1&pageSize=2");
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.Total >= 3);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.PageSize);
        Assert.True(page1.Items[0].OccurredAt >= page1.Items[1].OccurredAt);

        var authors = await manager.GetJsonAsync<List<ChangeLogAuthorDto>>("/api/manager/change-log/authors");
        Assert.Contains(authors, a => a.Email == ApiFactory.ManagerEmail);

        var filtered = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            $"/api/manager/change-log?actorUserId={authors.First(a => a.Email == ApiFactory.ManagerEmail).Id}&action=Created&page=1&pageSize=50");
        Assert.All(filtered.Items, i => Assert.Equal(ChangeLogAction.Created, i.Action));
        Assert.All(filtered.Items, i => Assert.Equal(ApiFactory.ManagerEmail, i.ActorEmail));
    }

    [Fact]
    public async Task Student_cannot_read_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/change-log")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/change-log/authors")).StatusCode);
    }

    [Fact]
    public async Task Change_log_has_no_mutation_endpoints()
    {
        var manager = await ManagerAsync();
        var post = await manager.PostAsync("/api/manager/change-log", new { });
        Assert.True(
            post.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Unexpected POST status: {post.StatusCode}");
        var del = await manager.DeleteAsync($"/api/manager/change-log/{Guid.CreateVersion7()}");
        Assert.True(
            del.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Unexpected DELETE status: {del.StatusCode}");
    }

    private static async Task<int> CountForEntity(ApiClient manager, Guid entityId)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            "/api/manager/change-log?entityType=Announcement&page=1&pageSize=200");
        return page.Items.Count(i => i.EntityId == entityId);
    }

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
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
            Assert.True((await userManager.AddToRoleAsync(user, Roles.Manager)).Succeeded);
        }
        var client = NewClient();
        await client.LoginAsync(email, password);
        return client;
    }

    private static Task<AnnouncementDetailDto> CreateAnnouncementAsync(
        ApiClient manager, Guid[] groupIds, string title = "Объявление", string body = "Текст объявления\nвторая строка",
        string? expiresAt = null) =>
        manager.PostJsonAsync<AnnouncementDetailDto>("/api/manager/announcements", new
        {
            title, body, isPinned = false, expiresAt, groupIds
        }, HttpStatusCode.Created);
}
