using System.Net;
using System.Net.Http.Json;
using MbaLms.Api.Controllers;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class AuthTests(ApiFactory factory) : TestBase(factory)
{
    public static TheoryData<string> ManagerUrls => new()
    {
        "/api/manager/groups", "/api/manager/students", "/api/manager/teachers", "/api/manager/disciplines",
        "/api/manager/periods", "/api/manager/lessons", "/api/manager/grades", "/api/manager/surveys",
        "/api/manager/exports/students", "/api/manager/exports/grades"
    };

    public static TheoryData<string> StudentUrls => new()
    {
        "/api/student/dashboard", "/api/student/schedule", "/api/student/grades", "/api/student/surveys"
    };

    [Theory]
    [MemberData(nameof(ManagerUrls))]
    [MemberData(nameof(StudentUrls))]
    [InlineData("/api/notifications")]
    public async Task Anonymous_requests_are_rejected_with_401(string url)
    {
        var client = await AnonymousAsync();
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ManagerUrls))]
    public async Task Student_cannot_access_manager_endpoints(string url)
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        var res = await student.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Student_cannot_modify_manager_data()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsync("/api/manager/groups", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsync($"/api/manager/groups/{group.Id}/archive")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StudentUrls))]
    public async Task Manager_has_no_student_cabinet(string url)
    {
        var manager = await ManagerAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_without_details()
    {
        var client = NewClient();
        var res = await client.TryLoginAsync(ApiFactory.ManagerEmail, "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("invalid_credentials", await res.ErrorCodeAsync());
        Assert.DoesNotContain("at MbaLms", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Me_returns_role_and_time_zone()
    {
        var manager = await ManagerAsync();
        var me = await manager.GetJsonAsync<MeDto>("/api/auth/me");
        Assert.Equal("Manager", me.Role);
        Assert.Equal("Europe/Moscow", me.TimeZone);
    }

    [Fact]
    public async Task State_changing_request_without_csrf_token_is_rejected()
    {
        var manager = await ManagerAsync();
        manager.Http.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        var res = await manager.Http.PostAsJsonAsync("/api/manager/groups", new { name = Unique("csrf") });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("csrf_failed", await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Logout_ends_session()
    {
        var manager = await ManagerAsync();
        (await manager.PostAsync("/api/auth/logout")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.GetAsync("/api/auth/me")).StatusCode);
    }
}
