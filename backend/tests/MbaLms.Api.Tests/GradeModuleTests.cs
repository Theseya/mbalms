using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class GradeModuleTests(ApiFactory factory) : TestBase(factory)
{
    private sealed record Sheet(ApiClient Manager, GroupDto Group, DisciplineDto Discipline, PeriodDto Period,
        StudentAccount Anna, StudentAccount Boris)
    {
        public string Url => $"/api/manager/grades/sheet/{Group.Id}/{Discipline.Id}/{Period.Id}";
    }

    private async Task<Sheet> SheetAsync()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var boris = await CreateStudentAsync(manager, group.Id, "Борисов");
        var anna = await CreateStudentAsync(manager, group.Id, "Аннина");
        return new Sheet(manager, group, await CreateDisciplineAsync(manager), await CreatePeriodAsync(manager), anna, boris);
    }

    private static object Entries(bool confirm = false, params (Guid StudentId, int? Value)[] entries) => new
    {
        entries = entries.Select(e => new { studentId = e.StudentId, value = e.Value }).ToArray(),
        confirmPublishedChanges = confirm
    };

    private static async Task<int> GradeNoticesAsync(ApiClient student) =>
        (await student.GetJsonAsync<List<NotificationDto>>("/api/notifications")).Count(n => n.Type == NotificationType.GradePublished);

    [Fact]
    public async Task Sheet_lists_every_student_of_the_group_only()
    {
        var s = await SheetAsync();
        var otherGroup = await CreateGroupAsync(s.Manager);
        await CreateStudentAsync(s.Manager, otherGroup.Id, "Чужой");

        var sheet = await s.Manager.GetJsonAsync<GradeSheetDto>(s.Url);
        Assert.Equal((s.Group.Name, s.Discipline.Name, s.Period.Name), (sheet.GroupName, sheet.DisciplineName, sheet.PeriodName));
        Assert.Equal([s.Anna.Id, s.Boris.Id], sheet.Rows.Select(r => r.StudentId));
        Assert.All(sheet.Rows, r => Assert.Null(r.GradeId));

        Assert.Equal(HttpStatusCode.NotFound, (await s.Manager.GetAsync($"/api/manager/grades/sheet/{Guid.NewGuid()}/{s.Discipline.Id}/{s.Period.Id}")).StatusCode);
    }

    [Fact]
    public async Task Sheet_saves_boundary_values_0_and_100_as_drafts()
    {
        var s = await SheetAsync();
        var res = await s.Manager.PutAsync(s.Url, Entries(false, (s.Anna.Id, 0), (s.Boris.Id, 100)));
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        var sheet = (await res.Content.ReadFromJsonAsync<GradeSheetDto>(ApiClient.Json))!;

        Assert.Equal([0, 100], sheet.Rows.Select(r => r.Value));
        Assert.All(sheet.Rows, r => Assert.Equal(GradeStatus.Draft, r.Status));
        Assert.Equal(0, await GradeNoticesAsync(await StudentAsync(s.Anna)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Sheet_rejects_values_outside_0_100_and_saves_nothing(int value)
    {
        var s = await SheetAsync();
        var res = await s.Manager.PutAsync(s.Url, Entries(false, (s.Anna.Id, 50), (s.Boris.Id, value)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("range", (await res.FieldErrorsAsync())["entries[1].value"]);
        Assert.All((await s.Manager.GetJsonAsync<GradeSheetDto>(s.Url)).Rows, r => Assert.Null(r.Value));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("50.5")]
    [InlineData("\"\"")]
    public async Task Single_grade_must_be_an_integer_from_0_to_100(string value)
    {
        var s = await SheetAsync();
        var body = $$"""{"studentId":"{{s.Anna.Id}}","disciplineId":"{{s.Discipline.Id}}","periodId":"{{s.Period.Id}}","value":{{value}}}""";
        var res = await s.Manager.Http.PostAsync("/api/manager/grades", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        // Built-in binder messages are English text; the client can translate only field codes.
        var codes = json.GetProperty("errors").EnumerateObject().SelectMany(p => p.Value.EnumerateArray()).Select(v => v.GetString()!).ToList();
        Assert.NotEmpty(codes);
        Assert.All(codes, c => Assert.Contains(c, FieldCodes.All));
        Assert.Empty(await s.Manager.GetJsonAsync<List<GradeDto>>($"/api/manager/grades?studentId={s.Anna.Id}"));
    }

    [Fact]
    public async Task Sheet_rejects_students_of_other_groups_and_repeated_students()
    {
        var s = await SheetAsync();
        var outsider = await CreateStudentAsync(s.Manager, (await CreateGroupAsync(s.Manager)).Id);

        var res = await s.Manager.PutAsync(s.Url, Entries(false, (s.Anna.Id, 60), (outsider.Id, 70)));
        Assert.Equal("not_found", (await res.FieldErrorsAsync())["entries[1].studentId"]);
        res = await s.Manager.PutAsync(s.Url, Entries(false, (s.Anna.Id, 60), (s.Anna.Id, 70)));
        Assert.Equal("duplicate", (await res.FieldErrorsAsync())["entries[1].studentId"]);

        Assert.Empty(await s.Manager.GetJsonAsync<List<GradeDto>>($"/api/manager/grades?studentId={outsider.Id}"));
        Assert.All((await s.Manager.GetJsonAsync<GradeSheetDto>(s.Url)).Rows, r => Assert.Null(r.Value));
    }

    [Fact]
    public async Task Sheet_updates_existing_grades_without_creating_duplicates()
    {
        var s = await SheetAsync();
        await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 60), (s.Boris.Id, 70)));
        var first = await s.Manager.GetJsonAsync<GradeSheetDto>(s.Url);

        // Null leaves a grade as is; an unchanged value writes no history.
        var second = await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 65), (s.Boris.Id, null)));
        Assert.Equal([65, 70], second.Rows.Select(r => r.Value));
        Assert.Equal(first.Rows.Select(r => r.GradeId), second.Rows.Select(r => r.GradeId));
        await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Boris.Id, 70)));

        var all = await s.Manager.GetJsonAsync<List<GradeDto>>($"/api/manager/grades?groupId={s.Group.Id}");
        Assert.Equal(2, all.Count);
        var borisHistory = await s.Manager.GetJsonAsync<List<GradeHistoryDto>>($"/api/manager/grades/{second.Rows[1].GradeId}/history");
        Assert.Equal([GradeChangeAction.Created], borisHistory.Select(h => h.Action));
    }

    [Fact]
    public async Task Changing_a_published_grade_requires_confirmation_and_is_audited()
    {
        var s = await SheetAsync();
        var sheet = await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 60)));
        var gradeId = sheet.Rows[0].GradeId!.Value;
        await s.Manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{gradeId}/publish");
        var anna = await StudentAsync(s.Anna);
        Assert.Equal(1, await GradeNoticesAsync(anna));

        foreach (var res in new[]
                 {
                     await s.Manager.PutAsync($"/api/manager/grades/{gradeId}", new { value = 75 }),
                     await s.Manager.PutAsync(s.Url, Entries(false, (s.Anna.Id, 75)))
                 })
        {
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("published_change_not_confirmed", await res.ErrorCodeAsync());
        }
        Assert.Equal(60, (await s.Manager.GetJsonAsync<GradeDto>($"/api/manager/grades/{gradeId}")).Value);
        Assert.Equal(60, Assert.Single(await anna.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades")).Value);
        Assert.Equal(1, await GradeNoticesAsync(anna));

        // Saving the same value is not a change and needs no confirmation.
        await s.Manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{gradeId}", new { value = 60 });

        await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(true, (s.Anna.Id, 75)));
        Assert.Equal(75, Assert.Single(await anna.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades")).Value);
        Assert.Equal(2, await GradeNoticesAsync(anna));
        await s.Manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{gradeId}", new { value = 80, confirmPublishedChange = true });
        Assert.Equal(3, await GradeNoticesAsync(anna));

        var history = await s.Manager.GetJsonAsync<List<GradeHistoryDto>>($"/api/manager/grades/{gradeId}/history");
        Assert.Equal((GradeChangeAction.Updated, 75, 80, GradeStatus.Published),
            (history[0].Action, history[0].OldValue, history[0].NewValue, history[0].NewStatus));
        Assert.Equal((GradeChangeAction.Updated, 60, 75), (history[1].Action, history[1].OldValue, history[1].NewValue));
        Assert.All(history, h => Assert.Equal(ApiFactory.ManagerEmail, h.ChangedBy));
        Assert.All(history, h => Assert.True(DateTimeOffset.UtcNow - h.ChangedAt < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Audit_entries_contain_only_who_when_and_what()
    {
        var s = await SheetAsync();
        var sheet = await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 60)));
        var res = await s.Manager.GetAsync($"/api/manager/grades/{sheet.Rows[0].GradeId}/history");
        var entry = (await res.Content.ReadFromJsonAsync<JsonElement>())[0];

        Assert.Equal(["id", "gradeId", "action", "oldValue", "newValue", "oldStatus", "newStatus", "changedBy", "changedAt"],
            entry.EnumerateObject().Select(p => p.Name));
        var raw = entry.GetRawText();
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", raw, StringComparison.Ordinal); // ASP.NET Identity password hash prefix
    }

    [Fact]
    public async Task Archived_group_sheet_is_readable_but_not_editable()
    {
        var s = await SheetAsync();
        await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 60)));
        await s.Manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{s.Group.Id}/archive");

        var sheet = await s.Manager.GetJsonAsync<GradeSheetDto>(s.Url);
        Assert.Equal(GroupStatus.Archived, sheet.GroupStatus);
        Assert.Equal(60, sheet.Rows[0].Value);
        var res = await s.Manager.PutAsync(s.Url, Entries(false, (s.Anna.Id, 70)));
        Assert.Equal("group_archived", await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Only_the_manager_can_create_change_or_publish_grades()
    {
        var s = await SheetAsync();
        var sheet = await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 60)));
        var gradeId = sheet.Rows[0].GradeId!.Value;
        var student = await StudentAsync(s.Anna);
        var anonymous = await AnonymousAsync();
        var create = new { studentId = s.Anna.Id, disciplineId = s.Discipline.Id, periodId = Guid.NewGuid(), value = 100 };

        foreach (var (client, expected) in new[] { (student, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(expected, (await client.GetAsync(s.Url)).StatusCode);
            Assert.Equal(expected, (await client.PutAsync(s.Url, Entries(true, (s.Anna.Id, 100)))).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/manager/grades", create)).StatusCode);
            Assert.Equal(expected, (await client.PutAsync($"/api/manager/grades/{gradeId}", new { value = 100, confirmPublishedChange = true })).StatusCode);
            Assert.Equal(expected, (await client.PostAsync($"/api/manager/grades/{gradeId}/publish")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/manager/grades/publish", new { ids = new[] { gradeId } })).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/manager/grades?studentId={s.Anna.Id}")).StatusCode);
        }

        var grade = await s.Manager.GetJsonAsync<GradeDto>($"/api/manager/grades/{gradeId}");
        Assert.Equal((60, GradeStatus.Draft), (grade.Value, grade.Status));
    }

    [Fact]
    public async Task Students_see_only_their_own_published_grades()
    {
        var s = await SheetAsync();
        var sheet = await s.Manager.PutJsonAsync<GradeSheetDto>(s.Url, Entries(false, (s.Anna.Id, 91), (s.Boris.Id, 55)));
        var anna = await StudentAsync(s.Anna);
        var boris = await StudentAsync(s.Boris);

        Assert.Empty(await anna.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"));
        Assert.Empty((await anna.GetJsonAsync<DashboardDto>("/api/student/dashboard")).RecentGrades);

        await s.Manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{sheet.Rows[0].GradeId}/publish");

        var annaGrades = await anna.GetJsonAsync<List<StudentGradeDto>>($"/api/student/grades?studentId={s.Boris.Id}");
        Assert.Equal((sheet.Rows[0].GradeId, 91), (Assert.Single(annaGrades).Id, annaGrades[0].Value));
        Assert.Equal(91, Assert.Single((await anna.GetJsonAsync<DashboardDto>("/api/student/dashboard")).RecentGrades).Value);
        // Boris's draft stays hidden even from Boris; Anna's published grade is not visible to him.
        Assert.Empty(await boris.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"));
        Assert.Equal(0, await GradeNoticesAsync(boris));
        Assert.Equal(1, await GradeNoticesAsync(anna));
    }
}
