using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class LessonImportTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Template_and_export_include_lesson_id()
    {
        var manager = await ManagerAsync();
        var template = await manager.GetAsync("/api/manager/imports/lessons/template");
        await template.EnsureStatusAsync(HttpStatusCode.OK);
        using var twb = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        Assert.Equal("LessonId", twb.Worksheet(1).Cell(1, 1).GetString());
        Assert.Equal("Дата", twb.Worksheet(1).Cell(1, 2).GetString());

        var group = await CreateGroupAsync(manager);
        var lesson = await CreateLessonAsync(manager, group.Id);
        using var ewb = new XLWorkbook(await (await manager.GetAsync($"/api/manager/exports/schedule?groupId={group.Id}"))
            .Content.ReadAsStreamAsync());
        Assert.Equal(lesson.Id.ToString(), ewb.Worksheet(1).Cell(2, 1).GetString());
    }

    [Fact]
    public async Task Preview_does_not_write_or_notify_confirm_creates_and_updates()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var existing = await CreateLessonAsync(manager, group.Id, "2030-06-01T10:00", "2030-06-01T11:30");
        var discipline = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.DisciplineId}");
        var teacher = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.TeacherId}");

        var beforeLessons = await CountLessonsAsync();
        var beforeNotes = await CountScheduleNotesAsync(student.Id);

        var preview = await PreviewAsync(manager, [
            (existing.Id.ToString(), "2030-06-01", "12:00", "13:30", group.Name, discipline.Name, teacher.FullName, null, null, null, null),
            (null, "2030-06-02", "09:00", "10:30", group.Name, discipline.Name, teacher.FullName, "Offline", "Hall", null, null)
        ]);
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);
        Assert.Equal(beforeLessons, await CountLessonsAsync());
        Assert.Equal(beforeNotes, await CountScheduleNotesAsync(student.Id));

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(beforeLessons + 1, await CountLessonsAsync());

        var updated = await manager.GetJsonAsync<LessonDto>($"/api/manager/lessons/{existing.Id}");
        Assert.Equal(new DateTime(2030, 6, 1, 12, 0, 0), updated.StartsAtLocal);
        Assert.Equal(new DateTime(2030, 6, 1, 13, 30, 0), updated.EndsAtLocal);

        // One summary notification for the group (not one per row).
        Assert.Equal(beforeNotes + 1, await CountScheduleNotesAsync(student.Id));
        var importNote = Assert.Single(await ScheduleNotesPayloadsAsync(student.Id),
            p => p.GetProperty("change").GetString() == "import");
        Assert.Equal(1, importNote.GetProperty("created").GetInt32());
        Assert.Equal(1, importNote.GetProperty("updated").GetInt32());
    }

    [Fact]
    public async Task No_op_update_does_not_notify()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var existing = await CreateLessonAsync(manager, group.Id, "2030-07-01T10:00", "2030-07-01T11:00");
        var discipline = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.DisciplineId}");
        var teacher = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.TeacherId}");
        var beforeNotes = await CountScheduleNotesAsync(student.Id);

        var preview = await PreviewAsync(manager, [
            (existing.Id.ToString(), "2030-07-01", "10:00", "11:00", group.Name, discipline.Name, teacher.FullName,
                existing.Format?.ToString(), existing.Location, existing.Comment, "Scheduled")
        ]);
        Assert.Equal(1, preview.UpdateCount);
        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(0, result.Updated);
        Assert.True(result.Skipped >= 1);
        Assert.Equal(beforeNotes, await CountScheduleNotesAsync(student.Id));
    }

    [Fact]
    public async Task Ambiguous_teacher_is_conflict_and_overlap_is_warning()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var d = await CreateDisciplineAsync(manager);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            db.Teachers.Add(new Teacher { Id = Guid.CreateVersion7(), LastName = "Один", FirstName = "Имя" });
            db.Teachers.Add(new Teacher { Id = Guid.CreateVersion7(), LastName = "Один", FirstName = "Имя" });
            await db.SaveChangesAsync();
        }

        var previewAmb = await PreviewAsync(manager, [
            (null, "2030-08-01", "10:00", "11:00", group.Name, d.Name, "Один Имя", null, null, null, null)
        ]);
        Assert.Equal(ImportRowAction.Conflict, Assert.Single(previewAmb.Rows).Action);

        var teacher = await CreateTeacherAsync(manager);
        var existing = await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", new
        {
            groupId = group.Id, disciplineId = d.Id, teacherId = teacher.Id,
            startsAt = "2030-08-10T10:00", endsAt = "2030-08-10T11:00"
        }, HttpStatusCode.Created);

        var previewOverlap = await PreviewAsync(manager, [
            (null, "2030-08-10", "10:30", "11:30", group.Name, d.Name, teacher.FullName, null, null, null, null)
        ]);
        var row = Assert.Single(previewOverlap.Rows);
        Assert.Equal(ImportRowAction.Create, row.Action);
        Assert.Contains(row.Warnings ?? [], w => w.Code == "overlap");
        Assert.Equal(existing.Id, existing.Id); // keep referenced
    }

    [Fact]
    public async Task End_before_start_and_unknown_lesson_id_are_errors()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var d = await CreateDisciplineAsync(manager);
        var t = await CreateTeacherAsync(manager);

        var badTime = await PreviewAsync(manager, [
            (null, "2030-09-01", "12:00", "11:00", group.Name, d.Name, t.FullName, null, null, null, null)
        ]);
        Assert.Equal(ImportRowAction.Error, Assert.Single(badTime.Rows).Action);

        var missing = await PreviewAsync(manager, [
            (Guid.CreateVersion7().ToString(), "2030-09-02", "10:00", "11:00", group.Name, d.Name, t.FullName, null, null, null, null)
        ]);
        Assert.Equal(ImportRowAction.Error, Assert.Single(missing.Rows).Action);
        Assert.Contains(missing.Rows[0].Errors, e => e.Code == FieldCodes.NotFound);
    }

    [Fact]
    public async Task Formula_is_rejected_with_coordinates()
    {
        var manager = await ManagerAsync();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Расписание");
        ws.Cell(1, 1).Value = "LessonId";
        ws.Cell(1, 2).Value = "Дата";
        ws.Cell(1, 3).Value = "Начало";
        ws.Cell(1, 4).Value = "Окончание";
        ws.Cell(1, 5).Value = "Группа";
        ws.Cell(1, 6).Value = "Дисциплина";
        ws.Cell(1, 7).Value = "Преподаватель";
        ws.Cell(2, 2).FormulaA1 = "=TODAY()";
        ws.Cell(2, 3).Value = "10:00";
        ws.Cell(2, 4).Value = "11:00";
        ws.Cell(2, 5).Value = "G";
        ws.Cell(2, 6).Value = "D";
        ws.Cell(2, 7).Value = "T";

        var preview = await PreviewFileAsync(manager, wb);
        Assert.Equal(string.Empty, preview.ImportId);
        var err = Assert.Single(preview.FileErrors);
        Assert.Equal(FieldCodes.FormulaNotAllowed, err.Code);
        Assert.Equal(2, err.Row);
        Assert.Equal("B", err.Column);
    }

    [Fact]
    public async Task Students_cannot_import_lessons()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var client = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/manager/imports/lessons/template")).StatusCode);
    }

    private async Task<int> CountLessonsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Data.AppDbContext>().Lessons.CountAsync();
    }

    private async Task<int> CountScheduleNotesAsync(Guid studentId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var userId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId).SingleAsync();
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.ScheduleChanged);
    }

    private async Task<List<JsonElement>> ScheduleNotesPayloadsAsync(Guid studentId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var userId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId).SingleAsync();
        var json = await db.Notifications.Where(n => n.UserId == userId && n.Type == NotificationType.ScheduleChanged)
            .Select(n => n.PayloadJson).ToListAsync();
        return json.Select(j => JsonDocument.Parse(j).RootElement.Clone()).ToList();
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager,
        (string? LessonId, string Date, string Start, string End, string Group, string Discipline, string Teacher,
            string? Format, string? Location, string? Comment, string? Status)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Расписание");
        ws.Cell(1, 1).Value = "LessonId";
        ws.Cell(1, 2).Value = "Дата";
        ws.Cell(1, 3).Value = "Начало";
        ws.Cell(1, 4).Value = "Окончание";
        ws.Cell(1, 5).Value = "Группа";
        ws.Cell(1, 6).Value = "Дисциплина";
        ws.Cell(1, 7).Value = "Преподаватель";
        ws.Cell(1, 8).Value = "Формат";
        ws.Cell(1, 9).Value = "Место / ссылка";
        ws.Cell(1, 10).Value = "Комментарий";
        ws.Cell(1, 11).Value = "Статус занятия";
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            if (r.LessonId is not null) ws.Cell(i + 2, 1).Value = r.LessonId;
            ws.Cell(i + 2, 2).Value = r.Date;
            ws.Cell(i + 2, 3).Value = r.Start;
            ws.Cell(i + 2, 4).Value = r.End;
            ws.Cell(i + 2, 5).Value = r.Group;
            ws.Cell(i + 2, 6).Value = r.Discipline;
            ws.Cell(i + 2, 7).Value = r.Teacher;
            if (r.Format is not null) ws.Cell(i + 2, 8).Value = r.Format;
            if (r.Location is not null) ws.Cell(i + 2, 9).Value = r.Location;
            if (r.Comment is not null) ws.Cell(i + 2, 10).Value = r.Comment;
            if (r.Status is not null) ws.Cell(i + 2, 11).Value = r.Status;
        }
        return await PreviewFileAsync(manager, wb);
    }

    private static async Task<ImportPreviewDto> PreviewFileAsync(ApiClient manager, XLWorkbook wb)
    {
        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "schedule.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/lessons/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/lessons/confirm", new { importId });
}
