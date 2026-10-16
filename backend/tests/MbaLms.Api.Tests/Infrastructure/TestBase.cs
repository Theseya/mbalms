using System.Net;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests.Infrastructure;

public record StudentAccount(Guid Id, Guid GroupId, string Email, string Password);

[Collection(ApiCollection.Name)]
public abstract class TestBase
{
    protected ApiFactory Factory { get; }

    protected TestBase(ApiFactory factory)
    {
        Factory = factory;
        // Controllable clock is shared across the collection; always start from wall-clock time.
        factory.Clock.ResetToSystem();
    }

    protected static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    protected ApiClient NewClient() =>
        new(Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false }));

    protected async Task<ApiClient> AnonymousAsync()
    {
        var client = NewClient();
        await client.RefreshCsrfAsync();
        return client;
    }

    protected async Task<ApiClient> ManagerAsync()
    {
        var client = NewClient();
        await client.LoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        return client;
    }

    protected async Task<ApiClient> StudentAsync(StudentAccount account)
    {
        var client = NewClient();
        await client.LoginAsync(account.Email, account.Password);
        return client;
    }

    protected static Task<GroupDto> CreateGroupAsync(ApiClient manager) =>
        manager.PostJsonAsync<GroupDto>("/api/manager/groups", new { name = Unique("Группа") }, HttpStatusCode.Created);

    protected async Task<(ApiClient Client, string Email, string Password)> CreateManagerAsync()
    {
        var email = $"{Unique("mgr")}@test.local";
        const string password = "Manager-Test-2";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Manager)).Succeeded);
        }
        var client = NewClient();
        await client.LoginAsync(email, password);
        return (client, email, password);
    }

    protected async Task<(ApiClient Client, Guid Id, string Email, string Password)> CreateOwnerUserAsync()
    {
        var email = $"{Unique("owner")}@test.local";
        const string password = "Owner-Test-1";
        Guid id;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Owner)).Succeeded);
            id = user.Id;
        }
        var client = NewClient();
        await client.LoginAsync(email, password);
        return (client, id, email, password);
    }

    protected async Task<(ApiClient Client, Guid Id, string Email, string Password)> CreateAccountAdminAsync()
    {
        var email = $"{Unique("acct")}@test.local";
        const string password = "Account-Admin-1";
        Guid id;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.AccountAdmin)).Succeeded);
            id = user.Id;
        }
        var client = NewClient();
        await client.LoginAsync(email, password);
        return (client, id, email, password);
    }

    protected async Task<int> CountEffectiveOwnersAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<AppTime>().UtcNow;
        var ownerRoleId = await db.Roles.Where(r => r.Name == Roles.Owner).Select(r => r.Id).SingleAsync();
        return await (
            from u in db.Users.AsNoTracking()
            join ur in db.UserRoles on u.Id equals ur.UserId
            where ur.RoleId == ownerRoleId
                  && (!u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd <= now)
            select u.Id).Distinct().CountAsync();
    }

    protected async Task RemoveOwnerRoleExceptAsync(params Guid[] keep)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var user in await users.GetUsersInRoleAsync(Roles.Owner))
        {
            if (keep.Contains(user.Id)) continue;
            Assert.True((await users.RemoveFromRoleAsync(user, Roles.Owner)).Succeeded);
        }
    }

    protected static async Task<StudentAccount> CreateStudentAsync(ApiClient manager, Guid groupId, string lastName = "Иванов")
    {
        var email = $"{Unique("student")}@test.local";
        const string password = "Student-Pass-1";
        var dto = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName, firstName = "Иван", email, groupId, password
        }, HttpStatusCode.Created);
        return new StudentAccount(dto.Id, groupId, email, password);
    }

    protected static Task<TeacherDto> CreateTeacherAsync(ApiClient manager) =>
        manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = "Петрова", firstName = Unique("Анна") }, HttpStatusCode.Created);

    protected static Task<DisciplineDto> CreateDisciplineAsync(ApiClient manager, string? description = null) =>
        manager.PostJsonAsync<DisciplineDto>("/api/manager/disciplines",
            new { name = Unique("Финансы"), description }, HttpStatusCode.Created);

    protected static Task<PeriodDto> CreatePeriodAsync(ApiClient manager) =>
        manager.PostJsonAsync<PeriodDto>("/api/manager/periods", new { name = Unique("Семестр") }, HttpStatusCode.Created);

    protected static async Task<LessonDto> CreateLessonAsync(ApiClient manager, Guid groupId,
        string startsAt = "2030-03-10T10:00", string endsAt = "2030-03-10T11:30")
    {
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);
        return await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", new
        {
            groupId, disciplineId = discipline.Id, teacherId = teacher.Id, startsAt, endsAt,
            format = "Online", location = "https://meet.example/abc"
        }, HttpStatusCode.Created);
    }
}
