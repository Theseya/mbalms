using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class AnnouncementTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Draft_is_invisible_to_students_until_published()
    {
        Factory.Clock.ResetToSystem();
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        var draft = await CreateAnnouncementAsync(manager, [group.Id], title: "Черновик");
        Assert.Equal(AnnouncementStatus.Draft, draft.Status);

        Assert.Empty(await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/announcements/{draft.Id}")).StatusCode);
        Assert.Empty((await student.GetJsonAsync<DashboardDto>("/api/student/dashboard")).RecentAnnouncements);

        var published = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{draft.Id}/publish");
        Assert.Equal(AnnouncementStatus.Published, published.Status);
        Assert.NotNull(published.PublishedAt);

        var list = await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements");
        Assert.Contains(list, a => a.Id == draft.Id);
        var detail = await student.GetJsonAsync<StudentAnnouncementDto>($"/api/student/announcements/{draft.Id}");
        Assert.Equal(draft.Title, detail.Title);
        Assert.Equal("Текст объявления\nвторая строка", detail.Body);

        var dash = await student.GetJsonAsync<DashboardDto>("/api/student/dashboard");
        Assert.Contains(dash.RecentAnnouncements, a => a.Id == draft.Id);
    }

    [Fact]
    public async Task Unpublish_hides_from_students_but_keeps_PublishedAt()
    {
        Factory.Clock.ResetToSystem();
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        var a = await CreateAnnouncementAsync(manager, [group.Id]);
        var published = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        var publishedAt = published.PublishedAt;

        var unpublished = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/unpublish");
        Assert.Equal(AnnouncementStatus.Unpublished, unpublished.Status);
        Assert.Equal(publishedAt, unpublished.PublishedAt);
        Assert.Empty(await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/announcements/{a.Id}")).StatusCode);
    }

    [Fact]
    public async Task Republish_sets_PublishedAt_again_but_idempotent_publish_does_not()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var a = await CreateAnnouncementAsync(manager, [group.Id]);

        var first = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero), first.PublishedAt);

        Factory.Clock.Advance(TimeSpan.FromHours(1));
        var again = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        Assert.Equal(first.PublishedAt, again.PublishedAt);

        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/unpublish");
        Factory.Clock.Advance(TimeSpan.FromHours(1));
        var third = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 14, 0, 0, TimeSpan.Zero), third.PublishedAt);
        Factory.Clock.ResetToSystem();
    }

    [Fact]
    public async Task Edit_of_published_updates_content_without_changing_PublishedAt()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 2, 1, 10, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var studentA = await StudentAsync(await CreateStudentAsync(manager, groupA.Id));
        var studentB = await StudentAsync(await CreateStudentAsync(manager, groupB.Id));

        var a = await CreateAnnouncementAsync(manager, [groupA.Id], title: "Было");
        var published = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        var publishedAt = published.PublishedAt;

        Factory.Clock.Advance(TimeSpan.FromHours(2));
        var updated = await manager.PutJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}", new
        {
            title = "Стало",
            body = "Новый текст",
            isPinned = true,
            expiresAt = (string?)null,
            groupIds = new[] { groupA.Id, groupB.Id }
        });

        Assert.Equal("Стало", updated.Title);
        Assert.Equal(publishedAt, updated.PublishedAt);
        Assert.True(updated.IsPinned);
        Assert.Equal(2, updated.Groups.Count);

        Assert.Equal("Стало", (await studentA.GetJsonAsync<StudentAnnouncementDto>($"/api/student/announcements/{a.Id}")).Title);
        Assert.Equal("Стало", (await studentB.GetJsonAsync<StudentAnnouncementDto>($"/api/student/announcements/{a.Id}")).Title);
        Factory.Clock.ResetToSystem();
    }

    [Fact]
    public async Task Group_isolation_hides_foreign_announcements()
    {
        Factory.Clock.ResetToSystem();
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var studentA = await StudentAsync(await CreateStudentAsync(manager, groupA.Id));
        var studentB = await StudentAsync(await CreateStudentAsync(manager, groupB.Id));

        var a = await CreateAnnouncementAsync(manager, [groupA.Id]);
        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");

        Assert.Single(await studentA.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));
        Assert.Empty(await studentB.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));
        Assert.Equal(HttpStatusCode.NotFound, (await studentB.GetAsync($"/api/student/announcements/{a.Id}")).StatusCode);
    }

    [Fact]
    public async Task Pinning_orders_before_newer_unpinned()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 3, 1, 10, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        var olderPinned = await CreateAnnouncementAsync(manager, [group.Id], title: "Pinned", pinned: true);
        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{olderPinned.Id}/publish");

        Factory.Clock.Advance(TimeSpan.FromHours(2));
        var newer = await CreateAnnouncementAsync(manager, [group.Id], title: "Newer");
        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{newer.Id}/publish");

        var list = await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements");
        Assert.Equal(["Pinned", "Newer"], list.Select(x => x.Title).ToArray());

        var dash = await student.GetJsonAsync<DashboardDto>("/api/student/dashboard");
        Assert.Equal("Pinned", dash.RecentAnnouncements[0].Title);
        Factory.Clock.ResetToSystem();
    }

    [Fact]
    public async Task Expiry_hides_from_students_without_changing_status()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 4, 1, 10, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        // expiresAt is wall-clock Europe/Moscow → 2030-04-01T15:00 MSK = 12:00 UTC
        var a = await CreateAnnouncementAsync(manager, [group.Id], expiresAt: "2030-04-01T15:00");
        var published = await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        Assert.False(published.IsExpired);
        Assert.Single(await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));

        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 4, 1, 12, 0, 1, TimeSpan.Zero));
        Assert.Empty(await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/announcements/{a.Id}")).StatusCode);

        var managerView = await manager.GetJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}");
        Assert.Equal(AnnouncementStatus.Published, managerView.Status);
        Assert.True(managerView.IsExpired);
        Factory.Clock.ResetToSystem();
    }

    [Fact]
    public async Task Publish_rejects_past_ExpiresAt()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 5, 1, 12, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var a = await CreateAnnouncementAsync(manager, [group.Id], expiresAt: "2030-05-01T12:00"); // MSK = 09:00 UTC < now
        var res = await manager.PostAsync($"/api/manager/announcements/{a.Id}/publish");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Factory.Clock.ResetToSystem();
    }

    [Fact]
    public async Task Save_requires_at_least_one_group_and_blocks_new_archived_assignment()
    {
        Factory.Clock.ResetToSystem();
        var manager = await ManagerAsync();
        var active = await CreateGroupAsync(manager);
        var archived = await CreateGroupAsync(manager);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{archived.Id}/archive");

        var empty = await manager.PostAsync("/api/manager/announcements", new
        {
            title = "X", body = "Y", isPinned = false, expiresAt = (string?)null, groupIds = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var bad = await manager.PostAsync("/api/manager/announcements", new
        {
            title = "X", body = "Y", isPinned = false, expiresAt = (string?)null, groupIds = new[] { archived.Id }
        });
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        Assert.Equal("group_archived", await bad.ErrorCodeAsync());

        var a = await CreateAnnouncementAsync(manager, [active.Id]);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{active.Id}/archive");
        // Existing archived link may be kept on edit.
        var kept = await manager.PutJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}", new
        {
            title = a.Title, body = a.Body, isPinned = false, expiresAt = (string?)null, groupIds = new[] { active.Id }
        });
        Assert.Equal(GroupStatus.Archived, Assert.Single(kept.Groups).Status);
    }

    [Fact]
    public async Task Delete_only_for_draft_or_unpublished()
    {
        Factory.Clock.ResetToSystem();
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var a = await CreateAnnouncementAsync(manager, [group.Id]);
        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");

        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/announcements/{a.Id}")).StatusCode);

        await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/unpublish");
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/announcements/{a.Id}")).StatusCode);
    }

    [Fact]
    public async Task Student_cannot_manage_announcements()
    {
        Factory.Clock.ResetToSystem();
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/announcements")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_recent_announcements_capped_at_three()
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero));
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        for (var i = 0; i < 4; i++)
        {
            Factory.Clock.SetUtcNow(new DateTimeOffset(2030, 6, 1, 10 + i, 0, 0, TimeSpan.Zero));
            var a = await CreateAnnouncementAsync(manager, [group.Id], title: $"A{i}");
            await manager.PostJsonAsync<AnnouncementDetailDto>($"/api/manager/announcements/{a.Id}/publish");
        }

        var dash = await student.GetJsonAsync<DashboardDto>("/api/student/dashboard");
        Assert.Equal(3, dash.RecentAnnouncements.Count);
        Assert.Equal(["A3", "A2", "A1"], dash.RecentAnnouncements.Select(x => x.Title).ToArray());

        var list = await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements");
        Assert.Equal(4, list.Count);
        Factory.Clock.ResetToSystem();
    }

    private static async Task<AnnouncementDetailDto> CreateAnnouncementAsync(
        ApiClient manager, Guid[] groupIds, string title = "Объявление", string body = "Текст объявления\nвторая строка",
        bool pinned = false, string? expiresAt = null) =>
        await manager.PostJsonAsync<AnnouncementDetailDto>("/api/manager/announcements", new
        {
            title, body, isPinned = pinned, expiresAt, groupIds
        }, HttpStatusCode.Created);
}
