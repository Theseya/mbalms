using System.Net;
using MbaLms.Api.Controllers.Manager;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MbaLms.Api.Tests.Infrastructure;

public record StudentAccount(Guid Id, Guid GroupId, string Email, string Password);

[Collection(ApiCollection.Name)]
public abstract class TestBase(ApiFactory factory)
{
    protected ApiFactory Factory { get; } = factory;

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
