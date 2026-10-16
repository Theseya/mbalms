using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class OwnerBootstrapTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Bootstrap_creates_owner_without_changing_managers_and_refuses_a_second_run()
    {
        string managerHash;
        string managerStamp;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var manager = await users.FindByEmailAsync(ApiFactory.ManagerEmail);
            Assert.NotNull(manager);
            managerHash = manager.PasswordHash!;
            managerStamp = manager.SecurityStamp!;
            Assert.Equal([Roles.Manager], await users.GetRolesAsync(manager));
        }

        await RemoveOwnerRoleExceptAsync();

        var email = $"{Unique("boot")}@test.local";
        const string password = "Bootstrap-Owner-1";
        OwnerBootstrapResult first;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<OwnerBootstrap>().RunAsync(email, password);
        }
        Assert.Equal(OwnerBootstrapStatus.Created, first.Status);
        Assert.NotNull(first.UserId);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var owner = await users.FindByEmailAsync(email);
            Assert.NotNull(owner);
            Assert.Equal([Roles.Owner], await users.GetRolesAsync(owner));

            var manager = await users.FindByEmailAsync(ApiFactory.ManagerEmail);
            Assert.NotNull(manager);
            Assert.Equal(managerHash, manager.PasswordHash);
            Assert.Equal(managerStamp, manager.SecurityStamp);
            Assert.Equal([Roles.Manager], await users.GetRolesAsync(manager));

            var second = await scope.ServiceProvider.GetRequiredService<OwnerBootstrap>()
                .RunAsync($"{Unique("boot2")}@test.local", password);
            Assert.Equal(OwnerBootstrapStatus.AlreadyExists, second.Status);
        }

        var client = NewClient();
        await client.LoginAsync(email, password);
        var me = await client.GetJsonAsync<MeDto>("/api/auth/me");
        Assert.Equal(Roles.Owner, me.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/manager/groups")).StatusCode);
    }
}
