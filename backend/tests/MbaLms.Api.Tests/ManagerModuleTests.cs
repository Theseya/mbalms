using System.Net;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class ManagerModuleTests(ApiFactory factory) : TestBase(factory)
{
    private const string Password = "Student-Pass-1";

    [Fact]
    public async Task Single_program_can_be_renamed_but_not_created_or_deleted()
    {
        var manager = await ManagerAsync();
        var original = await manager.GetJsonAsync<ProgramDto>("/api/manager/program");
        try
        {
            var renamed = await manager.PutJsonAsync<ProgramDto>("/api/manager/program", new { name = "  Executive MBA  " });
            Assert.Equal(original.Id, renamed.Id);
            Assert.Equal("Executive MBA", (await manager.GetJsonAsync<ProgramDto>("/api/manager/program")).Name);

            Assert.Equal("required", (await (await manager.PutAsync("/api/manager/program", new { name = "  " })).FieldErrorsAsync())["name"]);
            Assert.Equal("max_length", (await (await manager.PutAsync("/api/manager/program", new { name = new string('x', 201) })).FieldErrorsAsync())["name"]);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await manager.PostAsync("/api/manager/program", new { name = "Second" })).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await manager.DeleteAsync("/api/manager/program")).StatusCode);
        }
        finally
        {
            await manager.PutJsonAsync<ProgramDto>("/api/manager/program", new { name = original.Name });
        }
    }

    [Fact]
    public async Task Group_validation_errors_point_to_the_field()
    {
        var manager = await ManagerAsync();
        var existing = await CreateGroupAsync(manager);

        var res = await manager.PostAsync("/api/manager/groups", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("required", (await res.FieldErrorsAsync())["name"]);

        res = await manager.PostAsync("/api/manager/groups", new { name = Unique("G"), startDate = "2026-09-01", endDate = "2026-08-31" });
        Assert.Equal("end_before_start", (await res.FieldErrorsAsync())["endDate"]);

        res = await manager.PostAsync("/api/manager/groups", new { name = $" {existing.Name.ToUpperInvariant()} " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("duplicate", (await res.FieldErrorsAsync())["name"]);

        var other = await CreateGroupAsync(manager);
        res = await manager.PutAsync($"/api/manager/groups/{other.Id}", new { name = existing.Name });
        Assert.Equal("duplicate", (await res.FieldErrorsAsync())["name"]);

        // Saving a group under its own name is not a duplicate.
        await manager.PutJsonAsync<GroupDto>($"/api/manager/groups/{existing.Id}", new { name = existing.Name, startDate = "2026-09-01" });
    }

    [Fact]
    public async Task Discipline_name_must_be_unique_regardless_of_case()
    {
        var manager = await ManagerAsync();
        var existing = await CreateDisciplineAsync(manager);

        var res = await manager.PostAsync("/api/manager/disciplines", new { name = existing.Name.ToLowerInvariant() });
        Assert.Equal("duplicate", (await res.FieldErrorsAsync())["name"]);

        var other = await CreateDisciplineAsync(manager);
        res = await manager.PutAsync($"/api/manager/disciplines/{other.Id}", new { name = existing.Name });
        Assert.Equal("duplicate", (await res.FieldErrorsAsync())["name"]);

        await manager.PutJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.Id}", new { name = existing.Name, description = "Описание" });
    }

    [Fact]
    public async Task Teacher_requires_names_and_a_valid_optional_email()
    {
        var manager = await ManagerAsync();
        var errors = await (await manager.PostAsync("/api/manager/teachers", new { lastName = " ", email = "not-an-email" })).FieldErrorsAsync();
        Assert.Equal("required", errors["lastName"]);
        Assert.Equal("required", errors["firstName"]);
        Assert.Equal("email", errors["email"]);

        var teacher = await CreateTeacherAsync(manager);
        var updated = await manager.PutJsonAsync<TeacherDto>($"/api/manager/teachers/{teacher.Id}",
            new { lastName = "Сидорова", firstName = "Анна", middleName = "  " });
        Assert.Equal("Сидорова Анна", updated.FullName);
        Assert.Null(updated.MiddleName);
    }

    [Fact]
    public async Task Student_validation_errors_point_to_the_field()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var existing = await CreateStudentAsync(manager, group.Id);

        var errors = await (await manager.PostAsync("/api/manager/students", new { })).FieldErrorsAsync();
        Assert.Equal("required", errors["lastName"]);
        Assert.Equal("required", errors["firstName"]);
        Assert.Equal("required", errors["email"]);
        Assert.Equal("required", errors["groupId"]);

        object Body(string email, Guid groupId, string? password = Password) =>
            new { lastName = "Тест", firstName = "Тест", email, groupId, password };

        var res = await manager.PostAsync("/api/manager/students", Body($"{Unique("s")}@test.local", group.Id, password: null));
        Assert.Equal("required", (await res.FieldErrorsAsync())["password"]);

        res = await manager.PostAsync("/api/manager/students", Body("not-an-email", group.Id));
        Assert.Equal("email", (await res.FieldErrorsAsync())["email"]);

        res = await manager.PostAsync("/api/manager/students", Body($"{Unique("s")}@test.local", group.Id, "short"));
        Assert.Equal("password_weak", (await res.FieldErrorsAsync())["password"]);

        res = await manager.PostAsync("/api/manager/students", Body(existing.Email.ToUpperInvariant(), group.Id));
        Assert.Equal("duplicate", (await res.FieldErrorsAsync())["email"]);

        res = await manager.PostAsync("/api/manager/students", Body($"{Unique("s")}@test.local", Guid.NewGuid()));
        Assert.Equal("not_found", (await res.FieldErrorsAsync())["groupId"]);
    }

    [Fact]
    public async Task Student_can_be_moved_between_active_groups_but_not_into_an_archived_one()
    {
        var manager = await ManagerAsync();
        var from = await CreateGroupAsync(manager);
        var to = await CreateGroupAsync(manager);
        var archived = await CreateGroupAsync(manager);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{archived.Id}/archive");
        var account = await CreateStudentAsync(manager, from.Id);

        object Body(Guid groupId) => new { lastName = "Иванов", firstName = "Иван", email = account.Email, groupId };

        var moved = await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{account.Id}", Body(to.Id));
        Assert.Equal(to.Name, moved.GroupName);

        var res = await manager.PutAsync($"/api/manager/students/{account.Id}", Body(archived.Id));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("group_archived", await res.ErrorCodeAsync());
        Assert.Equal(to.Id, (await manager.GetJsonAsync<StudentDto>($"/api/manager/students/{account.Id}")).GroupId);

        // After the student's own group is archived, the profile can still be corrected.
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{to.Id}/archive");
        var corrected = await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{account.Id}",
            new { lastName = "Иванова", firstName = "Мария", email = account.Email, groupId = to.Id });
        Assert.Equal("Иванова Мария", corrected.FullName);
        Assert.Equal(GroupStatus.Archived, corrected.GroupStatus);
    }

    [Fact]
    public async Task Students_are_searched_and_paged_on_the_server()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var surname = Unique("Фам");
        foreach (var first in new[] { "Анна", "Борис", "Вера" })
            await CreateNamedStudentAsync(manager, group.Id, surname, first);
        await CreateNamedStudentAsync(manager, group.Id, Unique("Другой"), "Анна");

        var page1 = await manager.GetJsonAsync<PagedResult<StudentDto>>($"/api/manager/students?search={surname}&pageSize=2");
        Assert.Equal(3, page1.Total);
        Assert.Equal(["Анна", "Борис"], page1.Items.Select(s => s.FirstName));

        var page2 = await manager.GetJsonAsync<PagedResult<StudentDto>>($"/api/manager/students?search={surname}&pageSize=2&page=2");
        Assert.Equal(["Вера"], page2.Items.Select(s => s.FirstName));

        // Every word must match; the search is case-insensitive.
        var byTwoWords = await manager.GetJsonAsync<PagedResult<StudentDto>>(
            $"/api/manager/students?groupId={group.Id}&search={Uri.EscapeDataString($"{surname.ToUpperInvariant()} вер")}");
        Assert.Equal(["Вера"], byTwoWords.Items.Select(s => s.FirstName));

        var all = await manager.GetJsonAsync<PagedResult<StudentDto>>($"/api/manager/students?groupId={group.Id}");
        Assert.Equal(4, all.Total);
        Assert.Equal(Paging.DefaultPageSize, all.PageSize);

        // LIKE wildcards in the search are treated as plain text.
        foreach (var wildcard in new[] { "%", "_", @"\" })
        {
            var res = await manager.GetJsonAsync<PagedResult<StudentDto>>(
                $"/api/manager/students?groupId={group.Id}&search={Uri.EscapeDataString(wildcard)}");
            Assert.Equal(0, res.Total);
        }
    }

    [Fact]
    public async Task Search_by_email_part_finds_the_student()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var local = account.Email.Split('@')[0];

        var res = await manager.GetJsonAsync<PagedResult<StudentDto>>($"/api/manager/students?search={local[^6..]}");
        Assert.Equal(account.Id, Assert.Single(res.Items).Id);
    }

    [Theory]
    [InlineData("page=0", "page", "range")]
    [InlineData("pageSize=0", "pageSize", "range")]
    [InlineData("pageSize=201", "pageSize", "range")]
    public async Task Invalid_paging_is_rejected(string queryString, string field, string code)
    {
        var manager = await ManagerAsync();
        var res = await manager.GetAsync($"/api/manager/students?{queryString}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(code, (await res.FieldErrorsAsync())[field]);
    }

    [Fact]
    public async Task Too_long_search_is_rejected()
    {
        var manager = await ManagerAsync();
        var res = await manager.GetAsync($"/api/manager/students?search={new string('a', 101)}");
        Assert.Equal("max_length", (await res.FieldErrorsAsync())["search"]);
    }

    [Fact]
    public async Task Students_export_uses_the_same_search_as_the_table()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var surname = Unique("Экспорт");
        await CreateNamedStudentAsync(manager, group.Id, surname, "Анна");
        await CreateNamedStudentAsync(manager, group.Id, Unique("Прочий"), "Борис");

        var res = await manager.GetAsync($"/api/manager/exports/students?groupId={group.Id}&search={surname}");
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(1);
        Assert.Equal(surname, ws.Cell(2, 1).GetString());
        Assert.True(ws.Cell(3, 1).IsEmpty());
    }

    [Fact]
    public async Task Archive_and_restore_follow_the_status_and_keep_the_group_visible()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        await CreateStudentAsync(manager, group.Id);

        var archived = await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/archive");
        Assert.NotNull(archived.ArchivedAt);
        var again = await manager.PostAsync($"/api/manager/groups/{group.Id}/archive");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_status_transition", await again.ErrorCodeAsync());

        Assert.Contains(await manager.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All"), g => g.Id == group.Id);
        Assert.Equal(GroupStatus.Archived, (await manager.GetJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}")).Status);
        // Archiving never deletes: the archived group still cannot be removed while it has students.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.DeleteAsync($"/api/manager/groups/{group.Id}")).StatusCode);

        var restored = await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/restore");
        Assert.Equal(GroupStatus.Active, restored.Status);
        Assert.Null(restored.ArchivedAt);
        Assert.Equal(1, restored.StudentCount);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/manager/groups/{group.Id}/restore")).StatusCode);
    }

    [Fact]
    public async Task Empty_group_and_unused_reference_records_can_be_deleted()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var teacher = await CreateTeacherAsync(manager);
        var discipline = await CreateDisciplineAsync(manager);

        foreach (var url in new[] { $"/api/manager/groups/{group.Id}", $"/api/manager/teachers/{teacher.Id}", $"/api/manager/disciplines/{discipline.Id}" })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Student_cannot_use_the_manager_module()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));

        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/program")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PutAsync("/api/manager/program", new { name = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/students?search=a")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsync($"/api/manager/groups/{group.Id}/archive")).StatusCode);
        Assert.Equal(GroupStatus.Active, (await manager.GetJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}")).Status);
        Assert.NotEqual("Hacked", (await manager.GetJsonAsync<ProgramDto>("/api/manager/program")).Name);
    }

    private static Task<StudentDto> CreateNamedStudentAsync(ApiClient manager, Guid groupId, string lastName, string firstName) =>
        manager.PostJsonAsync<StudentDto>("/api/manager/students", new
        {
            lastName, firstName, email = $"{Unique("student")}@test.local", groupId, password = Password
        }, HttpStatusCode.Created);
}
