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

public class ReferenceImportChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Groups_preview_does_not_log_confirm_logs_create_update_skip()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("ImpG"), startDate = "2030-01-01", endDate = "2030-06-30" }, HttpStatusCode.Created);
        var noop = await manager.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("ImpSkip"), startDate = "2030-01-01", endDate = (string?)null }, HttpStatusCode.Created);
        var logsBefore = await TotalLogsDb();
        var existingLogs = await CountLogsDb(existing.Id);
        var noopLogs = await CountLogsDb(noop.Id);

        var newName = Unique("ImpGNew");
        var preview = await PreviewGroupsAsync(manager, [
            (existing.Name, "2030-02-01", "2030-07-31"),
            (newName, "2031-01-01", null),
            (noop.Name, "2030-01-01", null)
        ]);
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(2, preview.UpdateCount);
        Assert.Equal(logsBefore, await TotalLogsDb());

        var result = await ConfirmGroupsAsync(manager, preview.ImportId);
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

        var created = await db.Groups.AsNoTracking().SingleAsync(g => g.Name == newName);
        var createLog = await db.ChangeLogEntries.AsNoTracking().SingleAsync(e => e.EntityId == created.Id);
        Assert.Equal(ChangeLogAction.Created, createLog.Action);
        Assert.Equal("excelImport", ParseSource(createLog));
    }

    [Fact]
    public async Task Disciplines_confirm_logs_description_markers_and_preview_is_silent()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<DisciplineDto>("/api/manager/disciplines",
            new { name = Unique("ImpD"), description = "старое" }, HttpStatusCode.Created);
        var logsBefore = await TotalLogsDb();

        var newName = Unique("ImpDNew");
        var preview = await PreviewDisciplinesAsync(manager, [
            (existing.Name, "новое секретное"),
            (newName, null)
        ]);
        Assert.Equal(logsBefore, await TotalLogsDb());

        await ConfirmDisciplinesAsync(manager, preview.ImportId);

        await using var db = NewDb();
        var update = await db.ChangeLogEntries.AsNoTracking()
            .Where(e => e.EntityId == existing.Id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .LastAsync();
        Assert.Equal(ApiFactory.ManagerEmail, ParseActor(update));
        Assert.Contains("\"changed\"", update.ChangesJson);
        Assert.DoesNotContain("секрет", update.ChangesJson);
        Assert.DoesNotContain("новое", update.ChangesJson);

        var created = await db.Disciplines.AsNoTracking().SingleAsync(d => d.Name == newName);
        var createLog = await db.ChangeLogEntries.AsNoTracking().SingleAsync(e => e.EntityId == created.Id);
        Assert.Equal(ApiFactory.ManagerEmail, ParseActor(createLog));
        Assert.Equal("excelImport", ParseSource(createLog));
        Assert.DoesNotContain("description", createLog.ChangesJson);
    }

    [Fact]
    public async Task Disciplines_import_save_fail_rolls_back_all_rows()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<DisciplineDto>("/api/manager/disciplines",
            new { name = Unique("BatchD"), description = "keep" }, HttpStatusCode.Created);
        var newName = Unique("BatchDNew");
        var preview = await PreviewDisciplinesAsync(manager, [
            (existing.Name, "updated-secret"),
            (newName, "created-secret")
        ]);
        var logsBefore = await TotalLogsDb();
        var discBefore = await CountDisciplinesDb();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
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
        Assert.Equal(discBefore, await db.Disciplines.CountAsync());
        Assert.False(await db.Disciplines.AnyAsync(d => d.Name == newName));
        var unchanged = await db.Disciplines.AsNoTracking().SingleAsync(d => d.Id == existing.Id);
        Assert.Equal("keep", unchanged.Description);
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
    }

    [Fact]
    public async Task Groups_import_save_fail_rolls_back_all_rows()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("BatchG"), startDate = "2030-01-01", endDate = "2030-06-30" }, HttpStatusCode.Created);
        var newName = Unique("BatchGNew");
        var preview = await PreviewGroupsAsync(manager, [
            (existing.Name, "2030-03-01", "2030-08-01"),
            (newName, "2031-01-01", null)
        ]);
        var logsBefore = await TotalLogsDb();
        var groupsBefore = await CountGroupsDb();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/imports/groups/confirm", new { importId = preview.ImportId });
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
        Assert.Equal(groupsBefore, await db.Groups.CountAsync());
        Assert.False(await db.Groups.AnyAsync(g => g.Name == newName));
        var unchanged = await db.Groups.AsNoTracking().SingleAsync(g => g.Id == existing.Id);
        Assert.Equal(new DateOnly(2030, 1, 1), unchanged.StartDate);
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
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

    private async Task<int> CountGroupsDb()
    {
        await using var db = NewDb();
        return await db.Groups.CountAsync();
    }

    private async Task<int> CountDisciplinesDb()
    {
        await using var db = NewDb();
        return await db.Disciplines.CountAsync();
    }

    private static string? ParseActor(ChangeLogEntry e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.ContextJson);
        return doc.RootElement.TryGetProperty("actorEmail", out var a) ? a.GetString() : null;
    }

    private static string? ParseSource(ChangeLogEntry e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.ContextJson);
        return doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind != System.Text.Json.JsonValueKind.Null
            ? s.GetString()
            : null;
    }

    private static async Task<ImportPreviewDto> PreviewGroupsAsync(ApiClient manager,
        (string Name, string? Start, string? End)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Groups");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Дата начала";
        ws.Cell(1, 3).Value = "Дата окончания";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Name;
            if (rows[i].Start is not null) ws.Cell(i + 2, 2).Value = rows[i].Start;
            if (rows[i].End is not null) ws.Cell(i + 2, 3).Value = rows[i].End;
        }
        return await PreviewFileAsync(manager, "/api/manager/imports/groups/preview", wb);
    }

    private static async Task<ImportPreviewDto> PreviewDisciplinesAsync(ApiClient manager,
        (string Name, string? Description)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Disciplines");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Описание";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Name;
            if (rows[i].Description is not null) ws.Cell(i + 2, 2).Value = rows[i].Description;
        }
        return await PreviewFileAsync(manager, "/api/manager/imports/disciplines/preview", wb);
    }

    private static async Task<ImportPreviewDto> PreviewFileAsync(ApiClient manager, string url, XLWorkbook wb)
    {
        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "import.xlsx");
        var res = await manager.Http.PostAsync(url, content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmGroupsAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/groups/confirm", new { importId });

    private static async Task<ImportConfirmResultDto> ConfirmDisciplinesAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/disciplines/confirm", new { importId });
}
