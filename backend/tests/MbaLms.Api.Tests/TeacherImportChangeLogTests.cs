using System.Net;
using System.Text.Json;
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

public class TeacherImportChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Teachers_preview_does_not_log_confirm_logs_create_update_skip()
    {
        var manager = await ManagerAsync();
        var email = $"{Unique("imp")}@test.local";
        var existing = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = Unique("ImpT"), firstName = "Old", email }, HttpStatusCode.Created);
        var noop = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = Unique("ImpSkip"), firstName = "Same", email = (string?)null }, HttpStatusCode.Created);
        var logsBefore = await TotalLogsDb();
        var existingLogs = await CountLogsDb(existing.Id);
        var noopLogs = await CountLogsDb(noop.Id);

        var newEmail = $"{Unique("new")}@test.local";
        var preview = await PreviewAsync(manager, [
            (existing.LastName, "NewFirst", null, email),
            (Unique("ImpTNew"), "Nik", "Nikolaevich", newEmail),
            (noop.LastName, noop.FirstName, null, null)
        ]);
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(2, preview.UpdateCount);
        Assert.Equal(logsBefore, await TotalLogsDb());

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(2, result.Updated);

        await using var db = NewDb();
        Assert.Equal(existingLogs + 1, await db.ChangeLogEntries.CountAsync(e => e.EntityId == existing.Id));
        Assert.Equal(noopLogs, await db.ChangeLogEntries.CountAsync(e => e.EntityId == noop.Id));

        var update = await db.ChangeLogEntries.AsNoTracking()
            .Where(e => e.EntityId == existing.Id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .LastAsync();
        Assert.Equal(ChangeLogAction.Updated, update.Action);
        Assert.Equal(ApiFactory.ManagerEmail, ParseActor(update));
        Assert.Equal("excelImport", ParseSource(update));
        Assert.Contains("\"firstName\"", update.ChangesJson);
        // Teacher address must not appear anywhere in the stored ChangeLog payload.
        Assert.DoesNotContain(email, update.ChangesJson);
        Assert.DoesNotContain(email, update.ContextJson);
        Assert.DoesNotContain(email, PayloadBlob(update));

        var created = await db.Teachers.AsNoTracking()
            .SingleAsync(t => t.FirstName == "Nik" && t.MiddleName == "Nikolaevich");
        var createLog = await db.ChangeLogEntries.AsNoTracking().SingleAsync(e => e.EntityId == created.Id);
        Assert.Equal(ChangeLogAction.Created, createLog.Action);
        Assert.Equal("excelImport", ParseSource(createLog));
        Assert.DoesNotContain(newEmail, createLog.ChangesJson);
        Assert.DoesNotContain(newEmail, createLog.ContextJson);
        Assert.DoesNotContain(newEmail, PayloadBlob(createLog));
        Assert.Contains("\"email\"", createLog.ChangesJson);
        Assert.Contains("\"set\"", createLog.ChangesJson);
    }

    [Fact]
    public async Task Teachers_import_save_fail_rolls_back_all_rows()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = Unique("BatchT"), firstName = "Keep", email = (string?)null }, HttpStatusCode.Created);
        var newLast = Unique("BatchTNew");
        var preview = await PreviewAsync(manager, [
            (existing.LastName, "Changed", null, null),
            (newLast, "New", null, $"{Unique("n")}@test.local")
        ]);
        var logsBefore = await TotalLogsDb();
        var teachersBefore = await CountTeachersDb();
        var existingLogs = await CountLogsDb(existing.Id);

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/imports/teachers/confirm", new { importId = preview.ImportId });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.Equal(teachersBefore, await db.Teachers.CountAsync());
        Assert.False(await db.Teachers.AnyAsync(t => t.LastName == newLast));
        var unchanged = await db.Teachers.AsNoTracking().SingleAsync(t => t.Id == existing.Id);
        Assert.Equal("Keep", unchanged.FirstName);
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
        Assert.Equal(existingLogs, await db.ChangeLogEntries.CountAsync(e => e.EntityId == existing.Id));
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private async Task<int> TotalLogsDb()
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync();
    }

    private async Task<int> CountLogsDb(Guid entityId)
    {
        await using var db = NewDb();
        return await db.ChangeLogEntries.CountAsync(e => e.EntityId == entityId);
    }

    private async Task<int> CountTeachersDb()
    {
        await using var db = NewDb();
        return await db.Teachers.CountAsync();
    }

    private static string PayloadBlob(ChangeLogEntry e) =>
        $"{e.ChangesJson}\n{e.ContextJson}";

    private static string? ParseActor(ChangeLogEntry e)
    {
        using var doc = JsonDocument.Parse(e.ContextJson);
        return doc.RootElement.TryGetProperty("actorEmail", out var a) ? a.GetString() : null;
    }

    private static string? ParseSource(ChangeLogEntry e)
    {
        using var doc = JsonDocument.Parse(e.ContextJson);
        return doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind != JsonValueKind.Null
            ? s.GetString()
            : null;
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager,
        (string Last, string First, string? Middle, string? Email)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Teachers");
        ws.Cell(1, 1).Value = "Фамилия";
        ws.Cell(1, 2).Value = "Имя";
        ws.Cell(1, 3).Value = "Отчество";
        ws.Cell(1, 4).Value = "Email";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Last;
            ws.Cell(i + 2, 2).Value = rows[i].First;
            if (rows[i].Middle is not null) ws.Cell(i + 2, 3).Value = rows[i].Middle;
            if (rows[i].Email is not null) ws.Cell(i + 2, 4).Value = rows[i].Email;
        }
        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "teachers.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/teachers/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/teachers/confirm", new { importId });
}
