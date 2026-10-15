using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure.Email;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MbaLms.Api.Tests;

public class PasswordInviteTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Invite_captures_email_with_login_and_link_without_password_then_set_password_allows_login()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("inv")}@test.local";
        const string bootstrap = "Bootstrap-Pass-1";
        var student = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("Inv"),
            firstName = "Test",
            email,
            groupId = group.Id,
            password = bootstrap
        }, HttpStatusCode.Created);

        var invite = await manager.PostJsonAsync<StudentInviteResultDto>($"/api/manager/students/{student.Id}/invite", new { });
        Assert.True(invite.Queued);
        Assert.NotNull(invite.DevelopmentPreviewId);

        var preview = await manager.GetJsonAsync<CapturedEmailDto>($"/api/dev/mail/{invite.DevelopmentPreviewId}");
        Assert.Equal(email, preview.To);
        Assert.Equal(PasswordEmailComposer.PurposeInvite, preview.Purpose);
        Assert.Contains(email, preview.TextBody);
        Assert.Contains("/set-password?", preview.TextBody);
        Assert.Contains("http://127.0.0.1:5173/set-password?", preview.TextBody);
        Assert.DoesNotContain(bootstrap, preview.TextBody);

        var (linkEmail, token) = ParseSetupLink(preview.TextBody);
        Assert.Equal(email, linkEmail);

        var anon = await AnonymousAsync();
        const string newPassword = "Fresh-Invite-9";
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsync("/api/auth/set-password", new { email = linkEmail, token, newPassword })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await NewClient().TryLoginAsync(email, newPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().TryLoginAsync(email, bootstrap)).StatusCode);
    }

    [Fact]
    public async Task Set_password_rejects_reused_and_invalid_tokens()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("tok")}@test.local";
        var student = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("Tok"),
            firstName = "T",
            email,
            groupId = group.Id,
            password = "Student-Pass-1"
        }, HttpStatusCode.Created);

        var invite = await manager.PostJsonAsync<StudentInviteResultDto>($"/api/manager/students/{student.Id}/invite", new { });
        var preview = await manager.GetJsonAsync<CapturedEmailDto>($"/api/dev/mail/{invite.DevelopmentPreviewId}");
        var (_, token) = ParseSetupLink(preview.TextBody);

        var anon = await AnonymousAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsync("/api/auth/set-password",
                new { email, token, newPassword = "Once-Only-Pass-1" })).StatusCode);

        var reused = await anon.PostAsync("/api/auth/set-password",
            new { email, token, newPassword = "Second-Try-Pass-1" });
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("invalid_password_token", await reused.ErrorCodeAsync());

        var bad = await anon.PostAsync("/api/auth/set-password",
            new { email, token = "not-a-real-token", newPassword = "Whatever-Pass-1" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_password_token", await bad.ErrorCodeAsync());
    }

    [Fact]
    public async Task Set_password_rejects_token_after_security_stamp_change()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("exp")}@test.local";
        var student = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("Exp"),
            firstName = "E",
            email,
            groupId = group.Id,
            password = "Student-Pass-1"
        }, HttpStatusCode.Created);

        var invite = await manager.PostJsonAsync<StudentInviteResultDto>($"/api/manager/students/{student.Id}/invite", new { });
        var preview = await manager.GetJsonAsync<CapturedEmailDto>($"/api/dev/mail/{invite.DevelopmentPreviewId}");
        var (_, token) = ParseSetupLink(preview.TextBody);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException();
            await users.UpdateSecurityStampAsync(user);
        }

        var res = await (await AnonymousAsync()).PostAsync("/api/auth/set-password",
            new { email, token, newPassword = "After-Stamp-Pass-1" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("invalid_password_token", await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Forgot_password_does_not_reveal_account_and_allows_reset_when_user_exists()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("fg")}@test.local";
        await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("Fg"),
            firstName = "F",
            email,
            groupId = group.Id,
            password = "Student-Pass-1"
        }, HttpStatusCode.Created);

        var anon = await AnonymousAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsync("/api/auth/forgot-password", new { email = "nobody-here@test.local" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsync("/api/auth/forgot-password", new { email })).StatusCode);

        var preview = await manager.GetJsonAsync<CapturedEmailDto>($"/api/dev/mail/latest?to={Uri.EscapeDataString(email)}");
        Assert.Equal(PasswordEmailComposer.PurposeReset, preview.Purpose);
        Assert.Contains("http://127.0.0.1:5173/set-password?", preview.TextBody);
        var (_, token) = ParseSetupLink(preview.TextBody);

        const string newPassword = "Reset-Via-Mail-1";
        Assert.Equal(HttpStatusCode.NoContent,
            (await anon.PostAsync("/api/auth/set-password", new { email, token, newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NewClient().TryLoginAsync(email, newPassword)).StatusCode);
    }

    [Fact]
    public async Task Existing_manager_login_still_works()
    {
        Assert.Equal(HttpStatusCode.OK,
            (await NewClient().TryLoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword)).StatusCode);
    }

    [Fact]
    public async Task Invite_does_not_write_change_log_or_leak_token_in_api_payload()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("cl")}@test.local";
        var student = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("Cl"),
            firstName = "C",
            email,
            groupId = group.Id,
            password = "Student-Pass-1"
        }, HttpStatusCode.Created);

        var before = await manager.GetJsonAsync<ChangeLogPage>(
            $"/api/manager/change-log?entityType=Student&page=1&pageSize=100");
        var logsBefore = before.Items.Count(i => i.EntityId == student.Id);

        var invite = await manager.PostJsonAsync<StudentInviteResultDto>($"/api/manager/students/{student.Id}/invite", new { });
        var after = await manager.GetJsonAsync<ChangeLogPage>(
            $"/api/manager/change-log?entityType=Student&page=1&pageSize=100");
        Assert.Equal(logsBefore, after.Items.Count(i => i.EntityId == student.Id));

        var inviteJson = JsonSerializer.Serialize(invite);
        Assert.DoesNotContain("token=", inviteJson);
        Assert.DoesNotContain("/set-password", inviteJson);

        var preview = await manager.GetJsonAsync<CapturedEmailDto>($"/api/dev/mail/{invite.DevelopmentPreviewId}");
        var (_, token) = ParseSetupLink(preview.TextBody);
        Assert.DoesNotContain(token, inviteJson);
    }

    [Fact]
    public async Task Unavailable_mail_returns_mail_not_configured_on_invite()
    {
        await using var custom = Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender, UnavailableEmailSender>();
            });
        });

        var http = custom.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
        var api = new ApiClient(http);
        await api.LoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);

        var group = await api.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("G"), startDate = (string?)null, endDate = (string?)null }, HttpStatusCode.Created);
        var student = await api.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = Unique("Nm"),
            firstName = "N",
            email = $"{Unique("nm")}@test.local",
            groupId = group.Id,
            password = "Student-Pass-1"
        }, HttpStatusCode.Created);

        var res = await api.PostAsync($"/api/manager/students/{student.Id}/invite", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("mail_not_configured", await res.ErrorCodeAsync());
    }

    private static (string Email, string Token) ParseSetupLink(string body)
    {
        var m = Regex.Match(body, @"https?://\S*/set-password\?email=([^&\s]+)&token=([^\s]+)");
        Assert.True(m.Success, "setup link not found in email body");
        var token = m.Groups[2].Value.TrimEnd('.', ')', ']');
        return (Uri.UnescapeDataString(m.Groups[1].Value), Uri.UnescapeDataString(token));
    }

    private sealed record ChangeLogPage(List<ChangeLogRow> Items);
    private sealed record ChangeLogRow(Guid EntityId);
}
