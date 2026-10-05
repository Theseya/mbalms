using System.IO.Compression;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class ScheduleManagementTests(ApiFactory factory) : TestBase(factory)
{
    private sealed record Refs(GroupDto Group, TeacherDto Teacher, DisciplineDto Discipline);

    private static async Task<Refs> RefsAsync(ApiClient manager) =>
        new(await CreateGroupAsync(manager), await CreateTeacherAsync(manager), await CreateDisciplineAsync(manager));

    private static object Body(Refs r, string startsAt, string endsAt, object? status = null, string? comment = null,
        Guid? groupId = null, Guid? teacherId = null) => new
    {
        groupId = groupId ?? r.Group.Id, disciplineId = r.Discipline.Id, teacherId = teacherId ?? r.Teacher.Id,
        startsAt, endsAt, format = "Hybrid", location = "Ауд. 305 «Б»", comment, status
    };

    [Fact]
    public async Task Manager_creates_views_and_edits_a_lesson_with_every_field()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var created = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2030-04-01T09:30", "2030-04-01T11:00", comment: "Принести кейс"), HttpStatusCode.Created);

        Assert.Equal(LessonStatus.Scheduled, created.Status);
        Assert.Equal((r.Group.Id, r.Discipline.Id, r.Teacher.Id), (created.GroupId, created.DisciplineId, created.TeacherId));
        Assert.Equal(LessonFormat.Hybrid, created.Format);
        Assert.Equal(("Ауд. 305 «Б»", "Принести кейс"), (created.Location, created.Comment));

        var other = await CreateTeacherAsync(manager);
        var edited = await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{created.Id}", new
        {
            groupId = r.Group.Id, disciplineId = r.Discipline.Id, teacherId = other.Id,
            startsAt = "2030-04-02T14:00", endsAt = "2030-04-02T15:30", format = "Online",
            location = "https://meet.example/x", comment = "  ", status = "Cancelled"
        });
        Assert.Equal(other.Id, edited.TeacherId);
        Assert.Equal(new DateTime(2030, 4, 2, 14, 0, 0), edited.StartsAtLocal);
        Assert.Equal(new DateTime(2030, 4, 2, 15, 30, 0), edited.EndsAtLocal);
        Assert.Equal((LessonFormat.Online, LessonStatus.Cancelled), (edited.Format!.Value, edited.Status));
        Assert.Null(edited.Comment);

        var viewed = await manager.GetJsonAsync<LessonDto>($"/api/manager/lessons/{created.Id}");
        Assert.Equal(edited, viewed);
        Assert.Contains(await manager.GetJsonAsync<List<LessonDto>>($"/api/manager/lessons?groupId={r.Group.Id}"),
            l => l.Id == created.Id && l.Status == LessonStatus.Cancelled);
    }

    [Theory]
    [InlineData("2030-04-01T10:00", "2030-04-01T10:00", "endsAt", "end_before_start")]
    [InlineData("2030-04-01T10:00", "2030-04-01T09:59", "endsAt", "end_before_start")]
    [InlineData("2030-04-01T23:00", "2030-04-02T01:00", "endsAt", "not_same_day")]
    [InlineData("2030-04-01T07:00Z", "2030-04-01T08:00Z", "startsAt", "invalid")]
    [InlineData("2030-04-01T10:00+03:00", "2030-04-01T11:00+03:00", "startsAt", "invalid")]
    public async Task Invalid_lesson_times_are_rejected(string startsAt, string endsAt, string field, string code)
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var res = await manager.PostAsync("/api/manager/lessons", Body(r, startsAt, endsAt));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(code, (await res.FieldErrorsAsync())[field]);
        Assert.Empty(await manager.GetJsonAsync<List<LessonDto>>($"/api/manager/lessons?groupId={r.Group.Id}"));
    }

    [Fact]
    public async Task Editing_validates_times_and_keeps_the_lesson_unchanged()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var lesson = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2030-04-01T10:00", "2030-04-01T11:00"), HttpStatusCode.Created);

        var res = await manager.PutAsync($"/api/manager/lessons/{lesson.Id}", Body(r, "2030-04-01T12:00", "2030-04-01T11:00"));
        Assert.Equal("end_before_start", (await res.FieldErrorsAsync())["endsAt"]);
        Assert.Equal(lesson.EndsAt, (await manager.GetJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}")).EndsAt);
    }

    [Fact]
    public async Task Required_fields_unknown_references_and_unknown_status_are_rejected()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);

        var errors = await (await manager.PostAsync("/api/manager/lessons", new { })).FieldErrorsAsync();
        foreach (var field in new[] { "groupId", "disciplineId", "teacherId", "startsAt", "endsAt" })
            Assert.Equal("required", errors[field]);

        var res = await manager.PostAsync("/api/manager/lessons", Body(r, "2030-04-01T10:00", "2030-04-01T11:00", teacherId: Guid.NewGuid()));
        Assert.Equal("not_found", (await res.FieldErrorsAsync())["teacherId"]);

        res = await manager.PostAsync("/api/manager/lessons", Body(r, "2030-04-01T10:00", "2030-04-01T11:00", status: "Postponed"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Times_are_consistent_in_the_app_time_zone_across_storage_views_filters_and_export()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var account = await CreateStudentAsync(manager, r.Group.Id);
        var student = await StudentAsync(account);

        // 00:30 in Moscow is still the previous day in UTC.
        var lesson = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2030-03-10T00:30", "2030-03-10T01:30"), HttpStatusCode.Created);
        Assert.Equal(new DateTimeOffset(2030, 3, 9, 21, 30, 0, TimeSpan.Zero), lesson.StartsAt);
        Assert.Equal(new DateTime(2030, 3, 10, 0, 30, 0), lesson.StartsAtLocal);

        var own = Assert.Single(await student.GetJsonAsync<List<StudentLessonDto>>("/api/student/schedule"), l => l.Id == lesson.Id);
        Assert.Equal(new DateTime(2030, 3, 10, 0, 30, 0), own.StartsAtLocal);

        var day = $"/api/manager/lessons?groupId={r.Group.Id}";
        Assert.Single(await manager.GetJsonAsync<List<LessonDto>>($"{day}&from=2030-03-10&to=2030-03-10"));
        Assert.Empty(await manager.GetJsonAsync<List<LessonDto>>($"{day}&from=2030-03-09&to=2030-03-09"));
        Assert.Single(await student.GetJsonAsync<List<StudentLessonDto>>("/api/student/schedule?from=2030-03-10&to=2030-03-10"));

        using var wb = await DownloadAsync(manager, $"/api/manager/exports/schedule?groupId={r.Group.Id}");
        var ws = wb.Worksheet(1);
        Assert.Equal(new DateTime(2030, 3, 10), ws.Cell(2, 1).GetDateTime());
        Assert.Equal(new TimeSpan(0, 30, 0), ws.Cell(2, 2).Value.GetTimeSpan());
        Assert.Equal(new TimeSpan(1, 30, 0), ws.Cell(2, 3).Value.GetTimeSpan());
    }

    [Fact]
    public async Task Date_range_must_not_be_reversed()
    {
        var manager = await ManagerAsync();
        var res = await manager.GetAsync("/api/manager/lessons?from=2030-03-10&to=2030-03-09");
        Assert.Equal("end_before_start", (await res.FieldErrorsAsync())["to"]);
        res = await manager.GetAsync("/api/manager/exports/schedule?from=2030-03-10&to=2030-03-09");
        Assert.Equal("end_before_start", (await res.FieldErrorsAsync())["to"]);
    }

    [Fact]
    public async Task Student_sees_only_own_group_including_cancelled_lessons_marked_as_such()
    {
        var manager = await ManagerAsync();
        var mine = await RefsAsync(manager);
        var foreignGroup = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, mine.Group.Id));

        var scheduled = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(mine, "2031-05-01T10:00", "2031-05-01T11:00"), HttpStatusCode.Created);
        var cancelled = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(mine, "2031-04-30T10:00", "2031-04-30T11:00", status: "Cancelled"), HttpStatusCode.Created);
        var foreign = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(mine, "2031-05-01T10:00", "2031-05-01T11:00", groupId: foreignGroup.Id), HttpStatusCode.Created);

        var schedule = await student.GetJsonAsync<List<StudentLessonDto>>($"/api/student/schedule?groupId={foreignGroup.Id}");
        Assert.Equal([cancelled.Id, scheduled.Id], schedule.Select(l => l.Id));
        Assert.Equal(LessonStatus.Cancelled, schedule[0].Status);
        Assert.DoesNotContain(schedule, l => l.Id == foreign.Id);

        // The dashboard's next lesson skips cancelled ones.
        var dashboard = await student.GetJsonAsync<DashboardDto>("/api/student/dashboard");
        Assert.Equal(scheduled.Id, dashboard.NextLesson?.Id);
        Assert.DoesNotContain(dashboard.UpcomingLessons, l => l.Id == cancelled.Id);
    }

    [Fact]
    public async Task Cancelling_a_lesson_sends_a_cancellation_notice_to_the_group()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, r.Group.Id));
        var lesson = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2031-06-01T10:00", "2031-06-01T11:00"), HttpStatusCode.Created);

        await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", Body(r, "2031-06-01T10:00", "2031-06-01T11:00", status: "Cancelled"));
        await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}", Body(r, "2031-06-01T10:00", "2031-06-01T11:00", status: "Cancelled", comment: "Перенос"));

        var changes = (await student.GetJsonAsync<List<NotificationDto>>("/api/notifications"))
            .Where(n => n.Type == NotificationType.ScheduleChanged)
            .Select(n => n.Payload.GetProperty("change").GetString()).Reverse();
        Assert.Equal(["created", "cancelled", "updated"], changes);
    }

    [Fact]
    public async Task Overlaps_of_group_or_teacher_are_reported_but_do_not_block_saving()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var otherGroup = await CreateGroupAsync(manager);
        var otherTeacher = await CreateTeacherAsync(manager);
        var existing = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2031-07-01T10:00", "2031-07-01T11:30"), HttpStatusCode.Created);

        string Url(string from, string to, Guid group, Guid teacher, Guid? exclude = null) =>
            $"/api/manager/lessons/overlaps?startsAt=2031-07-01T{from}&endsAt=2031-07-01T{to}&groupId={group}&teacherId={teacher}"
            + (exclude is null ? "" : $"&excludeId={exclude}");

        var sameGroup = Assert.Single(await manager.GetJsonAsync<List<LessonOverlapDto>>(Url("11:00", "12:00", r.Group.Id, otherTeacher.Id)));
        Assert.Equal((existing.Id, true, false), (sameGroup.Id, sameGroup.SameGroup, sameGroup.SameTeacher));
        var sameTeacher = Assert.Single(await manager.GetJsonAsync<List<LessonOverlapDto>>(Url("09:00", "10:30", otherGroup.Id, r.Teacher.Id)));
        Assert.Equal((false, true), (sameTeacher.SameGroup, sameTeacher.SameTeacher));
        Assert.Equal(new DateTime(2031, 7, 1, 10, 0, 0), sameTeacher.StartsAtLocal);

        Assert.Empty(await manager.GetJsonAsync<List<LessonOverlapDto>>(Url("11:30", "12:30", r.Group.Id, r.Teacher.Id)));
        Assert.Empty(await manager.GetJsonAsync<List<LessonOverlapDto>>(Url("10:00", "11:30", r.Group.Id, r.Teacher.Id, existing.Id)));
        Assert.Empty(await manager.GetJsonAsync<List<LessonOverlapDto>>(Url("10:00", "11:30", otherGroup.Id, otherTeacher.Id)));

        // Saving an overlapping lesson is allowed; both lessons stay as entered.
        var overlapping = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2031-07-01T11:00", "2031-07-01T12:00"), HttpStatusCode.Created);
        Assert.Equal(2, (await manager.GetJsonAsync<List<LessonDto>>($"/api/manager/lessons?groupId={r.Group.Id}")).Count);

        // A cancelled lesson does not count as an overlap.
        await manager.PutJsonAsync<LessonDto>($"/api/manager/lessons/{overlapping.Id}",
            Body(r, "2031-07-01T11:00", "2031-07-01T12:00", status: "Cancelled"));
        Assert.Empty(await manager.GetJsonAsync<List<LessonOverlapDto>>(Url("11:30", "12:00", r.Group.Id, r.Teacher.Id)));
    }

    [Fact]
    public async Task Student_and_anonymous_cannot_manage_or_export_the_schedule()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, r.Group.Id));
        var anonymous = await AnonymousAsync();
        var lesson = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2031-08-01T10:00", "2031-08-01T11:00"), HttpStatusCode.Created);
        var body = Body(r, "2031-08-01T10:00", "2031-08-01T11:00", status: "Cancelled");

        foreach (var (client, expected) in new[] { (student, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(expected, (await client.GetAsync($"/api/manager/lessons?groupId={r.Group.Id}")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/manager/lessons/{lesson.Id}")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync("/api/manager/lessons/overlaps")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/manager/lessons", body)).StatusCode);
            Assert.Equal(expected, (await client.PutAsync($"/api/manager/lessons/{lesson.Id}", body)).StatusCode);
            Assert.Equal(expected, (await client.DeleteAsync($"/api/manager/lessons/{lesson.Id}")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync("/api/manager/exports/schedule")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/student/schedule")).StatusCode);
        Assert.Equal(LessonStatus.Scheduled, (await manager.GetJsonAsync<LessonDto>($"/api/manager/lessons/{lesson.Id}")).Status);
    }

    [Fact]
    public async Task Schedule_export_is_a_real_xlsx_workbook_and_not_csv()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", Body(r, "2030-04-01T09:30", "2030-04-01T11:00"), HttpStatusCode.Created);

        var res = await manager.GetAsync($"/api/manager/exports/schedule?groupId={r.Group.Id}");
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType?.MediaType);
        var fileName = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName;
        Assert.Matches(@"^schedule_\d{4}-\d{2}-\d{2}_\d{4}\.xlsx$", fileName);

        // An .xlsx is an OOXML zip package (signature "PK\x03\x04") with a workbook part, unlike plain-text CSV.
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("PK\u0003\u0004", Encoding.ASCII.GetString(bytes, 0, 4));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
        using var types = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
        Assert.Contains("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", await types.ReadToEndAsync());

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal("Расписание", wb.Worksheet(1).Name);
    }

    [Fact]
    public async Task Schedule_export_has_localized_headers_cyrillic_data_typed_cells_and_filters()
    {
        var manager = await ManagerAsync();
        var r = await RefsAsync(manager);
        var otherGroup = await CreateGroupAsync(manager);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", Body(r, "2030-05-01T10:00", "2030-05-01T11:00"), HttpStatusCode.Created);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2030-05-05T18:15", "2030-05-05T19:45", status: "Cancelled", comment: "=HYPERLINK(\"http://x\")"), HttpStatusCode.Created);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", Body(r, "2030-05-10T10:00", "2030-05-10T11:00"), HttpStatusCode.Created);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons",
            Body(r, "2030-05-05T10:00", "2030-05-05T11:00", groupId: otherGroup.Id), HttpStatusCode.Created);

        using var wb = await DownloadAsync(manager, $"/api/manager/exports/schedule?groupId={r.Group.Id}&from=2030-05-02&to=2030-05-09");
        var ws = wb.Worksheet(1);
        var headers = Enumerable.Range(1, 10).Select(c => ws.Cell(1, c).GetString()).ToArray();
        Assert.Equal(["Дата", "Начало (Europe/Moscow)", "Окончание (Europe/Moscow)", "Группа", "Дисциплина", "Преподаватель",
            "Формат", "Место / ссылка", "Комментарий", "Статус занятия"], headers);

        Assert.Equal(XLDataType.DateTime, ws.Cell(2, 1).DataType);
        Assert.Equal("05.05.2030", ws.Cell(2, 1).GetFormattedString());
        Assert.Equal(XLDataType.TimeSpan, ws.Cell(2, 2).DataType);
        Assert.Equal("18:15", ws.Cell(2, 2).GetFormattedString());
        Assert.Equal("19:45", ws.Cell(2, 3).GetFormattedString());
        Assert.Equal(r.Group.Name, ws.Cell(2, 4).GetString());
        Assert.Equal(r.Discipline.Name, ws.Cell(2, 5).GetString());
        Assert.Equal(r.Teacher.FullName, ws.Cell(2, 6).GetString());
        Assert.Equal(("Гибрид", "Ауд. 305 «Б»"), (ws.Cell(2, 7).GetString(), ws.Cell(2, 8).GetString()));
        Assert.Equal("=HYPERLINK(\"http://x\")", ws.Cell(2, 9).GetString());
        Assert.False(ws.Cell(2, 9).HasFormula);
        Assert.Equal("Отменено", ws.Cell(2, 10).GetString());
        Assert.True(ws.Cell(3, 1).IsEmpty());

        using var en = await DownloadAsync(manager, $"/api/manager/exports/schedule?groupId={r.Group.Id}&from=2030-05-01&to=2030-05-01&lang=en");
        var enWs = en.Worksheet(1);
        Assert.Equal(("Date", "Start (Europe/Moscow)", "Lesson status"), (enWs.Cell(1, 1).GetString(), enWs.Cell(1, 2).GetString(), enWs.Cell(1, 10).GetString()));
        Assert.Equal(("Hybrid", "Scheduled"), (enWs.Cell(2, 7).GetString(), enWs.Cell(2, 10).GetString()));
    }

    private static async Task<XLWorkbook> DownloadAsync(ApiClient client, string url)
    {
        var res = await client.GetAsync(url);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
    }
}
