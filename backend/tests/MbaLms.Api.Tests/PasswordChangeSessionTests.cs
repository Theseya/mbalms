using System.Net;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class PasswordChangeSessionTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Changing_password_ends_the_other_session_of_the_same_user()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var first = await StudentAsync(account);
        var second = await StudentAsync(account);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/student/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/student/dashboard")).StatusCode);

        const string nextPassword = "New-Pass-2026";
        var changed = await first.PostAsync("/api/auth/change-password", new
        {
            currentPassword = account.Password,
            newPassword = nextPassword
        });
        await changed.EnsureStatusAsync(HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/student/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);

        var staleLogin = NewClient();
        var stale = await staleLogin.TryLoginAsync(account.Email, account.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var fresh = NewClient();
        await fresh.LoginAsync(account.Email, nextPassword);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/student/dashboard")).StatusCode);
    }
}
