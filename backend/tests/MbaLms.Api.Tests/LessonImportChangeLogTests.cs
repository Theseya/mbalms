using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Tests;

public class LessonImportChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Preview_does_not_write_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var existing = await CreateLessonAsync(manager, group.Id, "2034-01-10T10:00", "2034-01-10T11:00");
        var discipline = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.DisciplineId}");
        var teacher = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.TeacherId}");
        var before = await TotalLogsAsync();

        await PreviewAsync(manager, [
            (null, "2034-01-11", "09:00", "10:00", group.Name, discipline.Name, teacher.FullName, "Online", "Hall", null, null),
            (existing.Id.ToString(), "2034-01-10", "12:00", "13:00", group.Name, discipline.Name, teacher.FullName, null, null, null, null)
        ]);

        Assert.Equal(before, await TotalLogsAsync());
    }

    [Fact]
    public async Task Confirm_logs_create_and_update_with_actor_and_excel_source()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var existing = await CreateLessonAsync(manager, group.Id, "2034-02-01T10:00", "2034-02-01T11:30");
        var discipline = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.DisciplineId}");
        var teacher = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.TeacherId}");
        var logsBeforeExisting = await CountLogsForAsync(existing.Id);

        var preview = await PreviewAsync(manager, [
            (existing.Id.ToString(), "2034-02-01", "12:00", "13:30", group.Name, discipline.Name, teacher.FullName,
                "Offline", "Import-Hall", "секретный комментарий", null),
            (null, "2034-02-02", "09:00", "10:30", group.Name, discipline.Name, teacher.FullName,
                "Online", "Import-New", null, null)
        ]);
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        await using var db = NewDb();
        var updatedLogs = await db.ChangeLogEntries.AsNoTracking()
            .Where(e => e.EntityId == existing.Id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync();
        Assert.Equal(logsBeforeExisting + 1, updatedLogs.Count);
        var updateEntry = updatedLogs[^1];
        Assert.Equal(ChangeLogAction.Updated, updateEntry.Action);
        Assert.Equal(ApiFactory.ManagerEmail, ParseActor(updateEntry));
        Assert.Equal("excelImport", ParseSource(updateEntry));
        using (var changes = System.Text.Json.JsonDocument.Parse(updateEntry.ChangesJson))
        {
            var fields = changes.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("field").GetString())
                .ToHashSet();
            Assert.Contains("startsAt", fields);
            Assert.Contains("endsAt", fields);
            Assert.Contains("location", fields);
            Assert.Contains("comment", fields);
            var location = changes.RootElement.EnumerateArray().Single(e => e.GetProperty("field").GetString() == "location");
            Assert.Equal("Import-Hall", location.GetProperty("new").GetString());
            var comment = changes.RootElement.EnumerateArray().Single(e => e.GetProperty("field").GetString() == "comment");
            Assert.Equal("changed", comment.GetProperty("new").GetString());
        }
        Assert.DoesNotContain("секретный", updateEntry.ChangesJson);

        var createdLesson = await db.Lessons.AsNoTracking()
            .SingleAsync(l => l.Location == "Import-New");
        var createEntry = await db.ChangeLogEntries.AsNoTracking()
            .SingleAsync(e => e.EntityId == createdLesson.Id);
        Assert.Equal(ChangeLogAction.Created, createEntry.Action);
        Assert.Equal(ApiFactory.ManagerEmail, ParseActor(createEntry));
        Assert.Equal("excelImport", ParseSource(createEntry));
    }

    [Fact]
    public async Task Confirm_no_op_update_does_not_add_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var existing = await CreateLessonAsync(manager, group.Id, "2034-03-01T10:00", "2034-03-01T11:00");
        var discipline = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.DisciplineId}");
        var teacher = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.TeacherId}");
        var before = await CountLogsForAsync(existing.Id);

        var preview = await PreviewAsync(manager, [
            (existing.Id.ToString(), "2034-03-01", "10:00", "11:00", group.Name, discipline.Name, teacher.FullName,
                existing.Format?.ToString(), existing.Location, existing.Comment, "Scheduled")
        ]);
        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(0, result.Updated);
        Assert.True(result.Skipped >= 1);
        Assert.Equal(before, await CountLogsForAsync(existing.Id));
    }

    [Fact]
    public async Task Failed_SaveChanges_on_confirm_rolls_back_lessons_and_change_log()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var existing = await CreateLessonAsync(manager, group.Id, "2034-04-01T10:00", "2034-04-01T11:00");
        var discipline = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{existing.DisciplineId}");
        var teacher = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.TeacherId}");
        var originalLocation = existing.Location;
        var logsBefore = await TotalLogsAsync();
        var lessonsBefore = await CountLessonsAsync();

        var preview = await PreviewAsync(manager, [
            (existing.Id.ToString(), "2034-04-01", "14:00", "15:00", group.Name, discipline.Name, teacher.FullName,
                null, "SHOULD-NOT-IMPORT", null, null),
            (null, "2034-04-02", "09:00", "10:00", group.Name, discipline.Name, teacher.FullName,
                null, "SHOULD-NOT-CREATE", null, null)
        ]);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/imports/lessons/confirm", new { importId = preview.ImportId });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.CheckViolation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.Equal(lessonsBefore, await db.Lessons.CountAsync());
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
        var row = await db.Lessons.AsNoTracking().SingleAsync(l => l.Id == existing.Id);
        Assert.Equal(originalLocation, row.Location);
        Assert.False(await db.Lessons.AnyAsync(l => l.Location == "SHOULD-NOT-IMPORT" || l.Location == "SHOULD-NOT-CREATE"));
    }

    [Fact]
    public async Task Second_confirm_is_rejected_without_duplicate_audit()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var d = await CreateDisciplineAsync(manager);
        var t = await CreateTeacherAsync(manager);

        var preview = await PreviewAsync(manager, [
            (null, "2034-05-01", "10:00", "11:00", group.Name, d.Name, t.FullName, null, "once-only", null, null)
        ]);
        var first = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, first.Created);
        var logsAfterFirst = await TotalLogsAsync();

        var second = await manager.PostAsync("/api/manager/imports/lessons/confirm", new { importId = preview.ImportId });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(ErrorCodes.ImportExpired, await second.ErrorCodeAsync());
        Assert.Equal(logsAfterFirst, await TotalLogsAsync());
        Assert.Equal(1, await NewDb().Lessons.CountAsync(l => l.Location == "once-only"));
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private async Task<int> TotalLogsAsync()
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync();
    }

    private async Task<int> CountLogsForAsync(Guid entityId)
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync(e => e.EntityId == entityId);
    }

    private async Task<int> CountLessonsAsync()
    {
        await using var db = NewDb();
        return await db.Lessons.CountAsync();
    }

    private static string? ParseActor(ChangeLogEntry e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.ContextJson);
        return doc.RootElement.TryGetProperty("actorEmail", out var a) ? a.GetString() : null;
    }

    private static string? ParseSource(ChangeLogEntry e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.ContextJson);
        return doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() : null;
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

    private static Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/lessons/confirm", new { importId });
}
