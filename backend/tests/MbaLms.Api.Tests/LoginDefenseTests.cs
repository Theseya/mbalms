using System.Net;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MbaLms.Api.Tests;

/// <summary>
/// Distinguishes per-account lockout (Identity, 5 failures) from per-IP login rate limiting.
/// The shared factory raises the login limiter so lockout can fire first; rate limiting uses a derived host.
/// </summary>
public class LoginDefenseTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Five_wrong_passwords_lock_only_that_account()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var attacker = NewClient();

        for (var i = 0; i < 5; i++)
        {
            var failed = await attacker.TryLoginAsync(account.Email, "Wrong-Pass-1");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, failed.StatusCode);
            var code = await failed.ErrorCodeAsync();
            Assert.True(code is ErrorCodes.InvalidCredentials or ErrorCodes.LockedOut, $"attempt {i + 1}: {code}");
        }

        var withCorrectPassword = await attacker.TryLoginAsync(account.Email, account.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, withCorrectPassword.StatusCode);
        Assert.Equal(ErrorCodes.LockedOut, await withCorrectPassword.ErrorCodeAsync());

        var other = NewClient();
        await other.LoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_rate_limit_rejects_further_attempts_from_the_same_client_with_429()
    {
        await using var limited = Factory.WithWebHostBuilder(b =>
            b.UseSetting("RateLimiting:LoginPermitsPerMinute", "3"));
        var client = new ApiClient(limited.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false
        }));

        for (var i = 0; i < 3; i++)
        {
            var res = await client.TryLoginAsync($"{Unique("rl")}@test.local", "Wrong-Pass-1");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal(ErrorCodes.InvalidCredentials, await res.ErrorCodeAsync());
        }

        var limitedOut = await client.TryLoginAsync($"{Unique("rl")}@test.local", "Wrong-Pass-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedOut.StatusCode);
    }
}
