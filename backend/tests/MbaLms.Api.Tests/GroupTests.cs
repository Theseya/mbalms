using System.Net;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class GroupTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Archiving_keeps_group_and_related_data_available_to_manager()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var lesson = await CreateLessonAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value = 90 }, HttpStatusCode.Created);

        var archived = await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/archive");
        Assert.Equal(GroupStatus.Archived, archived.Status);

        var active = await manager.GetJsonAsync<List<GroupDto>>("/api/manager/groups");
        var archivedList = await manager.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=Archived");
        Assert.DoesNotContain(active, g => g.Id == group.Id);
        Assert.Contains(archivedList, g => g.Id == group.Id && g.StudentCount == 1);

        Assert.Single((await manager.GetJsonAsync<PagedResult<StudentDto>>($"/api/manager/students?groupId={group.Id}")).Items);
        Assert.Contains(await manager.GetJsonAsync<List<LessonDto>>($"/api/manager/lessons?groupId={group.Id}"), l => l.Id == lesson.Id);
        Assert.Contains(await manager.GetJsonAsync<List<GradeDto>>($"/api/manager/grades?groupId={group.Id}"), g => g.Id == grade.Id);

        // Current lists are not mixed with archived data.
        Assert.Empty((await manager.GetJsonAsync<PagedResult<StudentDto>>($"/api/manager/students?search={student.Email}")).Items);
        Assert.Single((await manager.GetJsonAsync<PagedResult<StudentDto>>(
            $"/api/manager/students?search={student.Email}&includeArchived=true")).Items);
    }

    [Fact]
    public async Task Archived_group_does_not_accept_new_records()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/archive");

        var res = await manager.PostAsync("/api/manager/students", new
        {
            lastName = "Тест", firstName = "Тест", email = $"{Unique("s")}@test.local", groupId = group.Id, password = "Student-Pass-1"
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("group_archived", await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Group_with_students_cannot_be_deleted()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        await CreateStudentAsync(manager, group.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/groups/{group.Id}")).StatusCode);
    }

    [Fact]
    public async Task Created_reference_data_is_persisted()
    {
        var manager = await ManagerAsync();
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);

        var again = await ManagerAsync();
        Assert.Contains(await again.GetJsonAsync<List<TeacherDto>>("/api/manager/teachers"), t => t.Id == teacher.Id);
        Assert.Contains(await again.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines"), d => d.Id == discipline.Id);
    }
}
