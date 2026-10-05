using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class DisciplineImportTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Template_and_export_remain_valid_xlsx()
    {
        var manager = await ManagerAsync();
        var template = await manager.GetAsync("/api/manager/imports/disciplines/template");
        await template.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(ExcelExporter.ContentType, template.Content.Headers.ContentType?.MediaType);

        using var wb = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        Assert.Equal("Название", wb.Worksheet(1).Cell(1, 1).GetString());
        Assert.Equal("Описание", wb.Worksheet(1).Cell(1, 2).GetString());

        var export = await manager.GetAsync("/api/manager/exports/disciplines");
        await export.EnsureStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Preview_does_not_write_and_confirm_creates_and_updates()
    {
        var manager = await ManagerAsync();
        var existing = await CreateDisciplineAsync(manager, "old");
        var before = await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines");

        var preview = await PreviewAsync(manager, [
            (existing.Name, "updated-desc"),
            (Unique("Новая"), "desc")
        ]);
        Assert.False(string.IsNullOrEmpty(preview.ImportId));
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);

        var afterPreview = await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines");
        Assert.Equal(before.Count, afterPreview.Count);
        Assert.Equal("old", afterPreview.Single(d => d.Id == existing.Id).Description);

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        var after = await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines");
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal("updated-desc", after.Single(d => d.Id == existing.Id).Description);
        Assert.Contains(after, d => d.Name.StartsWith("Новая", StringComparison.Ordinal) && d.Description == "desc");
    }

    [Fact]
    public async Task Expired_preview_cannot_be_confirmed()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [(Unique("Истекает"), null)]);
        Factory.Services.GetRequiredService<ImportSessionStore>().ExpireForTests(preview.ImportId);

        var res = await manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await res.ErrorCodeAsync());
        Assert.DoesNotContain(await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines"),
            d => d.Name.StartsWith("Истекает", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Foreign_importId_is_rejected_and_owner_can_still_confirm()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [(Unique("Чужая"), null)]);

        var other = await CreateOtherManagerClientAsync();
        var foreign = await other.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        await foreign.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await foreign.ErrorCodeAsync());

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task Formula_cell_is_rejected_with_sheet_row_column()
    {
        var manager = await ManagerAsync();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Дисциплины");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Описание";
        ws.Cell(2, 1).FormulaA1 = "=\"Fin\"&\"X\"";
        ws.Cell(2, 2).Value = "x";

        var preview = await PreviewFileAsync(manager, wb);
        Assert.Equal(string.Empty, preview.ImportId);
        Assert.NotEmpty(preview.FileErrors);
        var err = Assert.Single(preview.FileErrors);
        Assert.Equal(FieldCodes.FormulaNotAllowed, err.Code);
        Assert.Equal("Дисциплины", err.Sheet);
        Assert.Equal(2, err.Row);
        Assert.Equal("A", err.Column);
    }

    [Fact]
    public async Task Ambiguous_case_variants_in_database_are_conflicts()
    {
        var manager = await ManagerAsync();
        var baseName = Unique("Amb");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            db.Disciplines.Add(new Discipline { Id = Guid.CreateVersion7(), Name = baseName.ToUpperInvariant() });
            db.Disciplines.Add(new Discipline { Id = Guid.CreateVersion7(), Name = baseName.ToLowerInvariant() });
            await db.SaveChangesAsync();
        }

        var preview = await PreviewAsync(manager, [(baseName, null)]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Conflict, row.Action);
        Assert.Contains(row.Errors, e => e.Code == FieldCodes.Ambiguous);
        Assert.Equal(0, preview.CreateCount);
        Assert.Equal(0, preview.UpdateCount);
    }

    [Fact]
    public async Task Duplicate_names_inside_file_are_conflicts()
    {
        var manager = await ManagerAsync();
        var name = Unique("Dup");
        var preview = await PreviewAsync(manager, [(name, "a"), (name.ToUpperInvariant(), "b")]);
        Assert.Equal(2, preview.ConflictCount);
        Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Conflict, r.Action));
    }

    [Fact]
    public async Task Repeat_confirm_does_not_apply_twice()
    {
        var manager = await ManagerAsync();
        var name = Unique("Once");
        var preview = await PreviewAsync(manager, [(name, null)]);
        Assert.Equal(1, (await ConfirmAsync(manager, preview.ImportId)).Created);

        var before = (await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines"))
            .Count(d => d.Name == name);

        var second = await manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        await second.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await second.ErrorCodeAsync());

        var after = (await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines"))
            .Count(d => d.Name == name);
        Assert.Equal(before, after);
        Assert.Equal(1, after);
    }

    [Fact]
    public async Task Parallel_confirm_applies_only_once()
    {
        var manager = await ManagerAsync();
        var name = Unique("Parallel");
        var preview = await PreviewAsync(manager, [(name, null)]);

        var t1 = manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        var t2 = manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        var results = await Task.WhenAll(t1, t2);

        var statuses = results.Select(r => r.StatusCode).OrderBy(s => s).ToArray();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.BadRequest, statuses);
        Assert.Equal(1, (await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines")).Count(d => d.Name == name));
    }

    [Fact]
    public async Task Failed_confirm_consumes_importId()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [(Unique("CreateMe"), null)]);
        // Force a confirm-time conflict: insert same name after preview.
        var stolen = preview.Rows.Single(r => r.Action == ImportRowAction.Create).Values["name"]!;
        await manager.PostJsonAsync<DisciplineDto>("/api/manager/disciplines", new { name = stolen }, HttpStatusCode.Created);

        var res = await manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);

        var retry = await manager.PostAsync("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });
        await retry.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await retry.ErrorCodeAsync());
        Assert.Equal(1, (await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines")).Count(d => d.Name == stolen));
    }

    [Fact]
    public async Task Students_cannot_import_disciplines()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var client = await StudentAsync(student);

        var res = await client.GetAsync("/api/manager/imports/disciplines/template");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private async Task<ApiClient> CreateOtherManagerClientAsync()
    {
        var email = $"{Unique("mgr")}@test.local";
        const string password = "Manager-Test-2";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Manager)).Succeeded);
        }
        var client = NewClient();
        await client.LoginAsync(email, password);
        return client;
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager, (string Name, string? Description)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Дисциплины");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Описание";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Name;
            if (rows[i].Description is not null) ws.Cell(i + 2, 2).Value = rows[i].Description;
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
        content.Add(file, "file", "disciplines.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/disciplines/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/disciplines/confirm", new { importId });
}
