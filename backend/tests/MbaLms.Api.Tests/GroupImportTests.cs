using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class GroupImportTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Template_has_name_and_dates_only()
    {
        var manager = await ManagerAsync();
        var template = await manager.GetAsync("/api/manager/imports/groups/template");
        await template.EnsureStatusAsync(HttpStatusCode.OK);

        using var wb = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(1);
        Assert.Equal("Название", ws.Cell(1, 1).GetString());
        Assert.Equal("Дата начала", ws.Cell(1, 2).GetString());
        Assert.Equal("Дата окончания", ws.Cell(1, 3).GetString());
        Assert.True(ws.Cell(1, 4).IsEmpty());
    }

    [Fact]
    public async Task Preview_does_not_write_and_confirm_creates_and_updates()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("MBA-1"), startDate = "2030-01-01", endDate = "2030-06-30" }, HttpStatusCode.Created);
        var before = await manager.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All");

        var newName = Unique("MBA-2");
        var preview = await PreviewAsync(manager, [
            (existing.Name, "2030-02-01", "2030-07-31"),
            (newName, "2031-01-01", null)
        ]);
        Assert.False(string.IsNullOrEmpty(preview.ImportId));
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);

        var afterPreview = await manager.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All");
        Assert.Equal(before.Count, afterPreview.Count);
        Assert.Equal(existing.StartDate, afterPreview.Single(g => g.Id == existing.Id).StartDate);

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        var after = await manager.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All");
        Assert.Equal(before.Count + 1, after.Count);
        var updated = after.Single(g => g.Id == existing.Id);
        Assert.Equal(new DateOnly(2030, 2, 1), updated.StartDate);
        Assert.Equal(new DateOnly(2030, 7, 31), updated.EndDate);
        var created = after.Single(g => g.Name == newName);
        Assert.Equal(GroupStatus.Active, created.Status);
        Assert.Equal(new DateOnly(2031, 1, 1), created.StartDate);
        Assert.Null(created.EndDate);
    }

    [Fact]
    public async Task Update_of_archived_group_keeps_archived_status()
    {
        var manager = await ManagerAsync();
        var group = await manager.PostJsonAsync<GroupDto>("/api/manager/groups",
            new { name = Unique("Arch"), startDate = "2030-01-01", endDate = (string?)null }, HttpStatusCode.Created);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/archive");

        var preview = await PreviewAsync(manager, [(group.Name, "2030-03-01", "2030-09-01")]);
        Assert.Equal(ImportRowAction.Update, Assert.Single(preview.Rows).Action);

        await ConfirmAsync(manager, preview.ImportId);
        var after = await manager.GetJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}");
        Assert.Equal(GroupStatus.Archived, after.Status);
        Assert.NotNull(after.ArchivedAt);
        Assert.Equal(new DateOnly(2030, 3, 1), after.StartDate);
        Assert.Equal(new DateOnly(2030, 9, 1), after.EndDate);
    }

    [Fact]
    public async Task End_before_start_is_error()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [(Unique("BadDates"), "2030-06-01", "2030-01-01")]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Code == FieldCodes.EndBeforeStart && e.Field == "endDate");
        Assert.Equal(0, preview.CreateCount);
    }

    [Fact]
    public async Task Ambiguous_case_variants_in_database_are_conflicts()
    {
        var manager = await ManagerAsync();
        var baseName = Unique("Amb");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var programId = db.Programs.Select(p => p.Id).First();
            db.Groups.Add(new Group { Id = Guid.CreateVersion7(), ProgramId = programId, Name = baseName.ToUpperInvariant() });
            db.Groups.Add(new Group { Id = Guid.CreateVersion7(), ProgramId = programId, Name = baseName.ToLowerInvariant() });
            await db.SaveChangesAsync();
        }

        var preview = await PreviewAsync(manager, [(baseName, null, null)]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Conflict, row.Action);
        Assert.Contains(row.Errors, e => e.Code == FieldCodes.Ambiguous);
    }

    [Fact]
    public async Task Duplicate_names_inside_file_are_conflicts()
    {
        var manager = await ManagerAsync();
        var name = Unique("Dup");
        var preview = await PreviewAsync(manager, [(name, null, null), (name.ToUpperInvariant(), "2030-01-01", null)]);
        Assert.Equal(2, preview.ConflictCount);
        Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Conflict, r.Action));
    }

    [Fact]
    public async Task Formula_cell_is_rejected_with_coordinates()
    {
        var manager = await ManagerAsync();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Группы");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Дата начала";
        ws.Cell(1, 3).Value = "Дата окончания";
        ws.Cell(2, 1).FormulaA1 = "=\"G\"&\"1\"";
        ws.Cell(2, 2).Value = "2030-01-01";

        var preview = await PreviewFileAsync(manager, wb);
        Assert.Equal(string.Empty, preview.ImportId);
        var err = Assert.Single(preview.FileErrors);
        Assert.Equal(FieldCodes.FormulaNotAllowed, err.Code);
        Assert.Equal(2, err.Row);
        Assert.Equal("A", err.Column);
    }

    [Fact]
    public async Task Expired_importId_cannot_be_confirmed()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [(Unique("Expire"), null, null)]);
        Factory.Services.GetRequiredService<ImportSessionStore>().ExpireForTests(preview.ImportId);

        var res = await manager.PostAsync("/api/manager/imports/groups/confirm", new { importId = preview.ImportId });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Students_cannot_import_groups()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var client = await StudentAsync(student);

        var res = await client.GetAsync("/api/manager/imports/groups/template");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager,
        (string Name, string? Start, string? End)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Группы");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Дата начала";
        ws.Cell(1, 3).Value = "Дата окончания";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Name;
            if (rows[i].Start is not null) ws.Cell(i + 2, 2).Value = rows[i].Start;
            if (rows[i].End is not null) ws.Cell(i + 2, 3).Value = rows[i].End;
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
        content.Add(file, "file", "groups.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/groups/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/groups/confirm", new { importId });
}
