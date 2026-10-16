using System.Net;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class OverpostTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Grade_create_ignores_status_and_stays_a_draft()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);

        var created = await manager.PostJsonAsync<GradeDto>("/api/manager/grades", new
        {
            studentId = student.Id,
            disciplineId = discipline.Id,
            periodId = period.Id,
            value = 77,
            status = "Published",
            publishedAt = "2020-01-01T00:00:00Z"
        }, HttpStatusCode.Created);

        Assert.Equal(77, created.Value);
        Assert.Equal(GradeStatus.Draft, created.Status);
        Assert.Null(created.PublishedAt);
        Assert.Equal(student.Id, created.StudentId);
        Assert.Empty(await (await StudentAsync(student)).GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"));
    }

    [Fact]
    public async Task Grade_update_ignores_status_and_foreign_studentId()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var owner = await CreateStudentAsync(manager, group.Id, "Владелец");
        var other = await CreateStudentAsync(manager, group.Id, "Чужой");
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = owner.Id, disciplineId = discipline.Id, periodId = period.Id, value = 40 },
            HttpStatusCode.Created);

        var updated = await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new
        {
            value = 41,
            status = "Published",
            studentId = other.Id,
            confirmPublishedChange = true
        });

        Assert.Equal(41, updated.Value);
        Assert.Equal(GradeStatus.Draft, updated.Status);
        Assert.Equal(owner.Id, updated.StudentId);
        Assert.Null(updated.PublishedAt);
        Assert.Empty(await (await StudentAsync(owner)).GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"));
        Assert.Empty(await (await StudentAsync(other)).GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"));
    }

    [Fact]
    public async Task Student_create_ignores_role_and_does_not_grant_manager()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("role")}@test.local";
        const string password = "Student-Pass-1";

        var created = await manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName = "Ролева",
            firstName = "Анна",
            email,
            groupId = group.Id,
            password,
            role = Roles.Manager
        }, HttpStatusCode.Created);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.Equal([Roles.Student], await users.GetRolesAsync(user));
        }

        var signedIn = NewClient();
        await signedIn.LoginAsync(email, password);
        var me = await signedIn.GetJsonAsync<MeDto>("/api/auth/me");
        Assert.Equal(Roles.Student, me.Role);
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.GetAsync("/api/manager/groups")).StatusCode);
        Assert.Equal(created.Email, me.Email);
    }

    [Fact]
    public async Task Announcement_create_ignores_status_and_stays_a_draft()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var created = await manager.PostJsonAsync<AnnouncementDetailDto>("/api/manager/announcements", new
        {
            title = Unique("Overpost"),
            body = "Черновик",
            isPinned = false,
            groupIds = new[] { group.Id },
            status = "Published"
        }, HttpStatusCode.Created);

        Assert.Equal(AnnouncementStatus.Draft, created.Status);
        Assert.Null(created.PublishedAt);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Empty(await student.GetJsonAsync<List<StudentAnnouncementDto>>("/api/student/announcements"));
    }

    [Fact]
    public async Task Student_cannot_create_a_grade_by_posting_manager_fields()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var student = await StudentAsync(account);
        await using var scope = Factory.Services.CreateAsyncScope();
        var before = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Grades.CountAsync();

        var res = await student.PostAsync("/api/manager/grades", new
        {
            studentId = account.Id,
            disciplineId = discipline.Id,
            periodId = period.Id,
            value = 100,
            status = "Published"
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        await using var afterScope = Factory.Services.CreateAsyncScope();
        Assert.Equal(before, await afterScope.ServiceProvider.GetRequiredService<AppDbContext>().Grades.CountAsync());
    }
}
