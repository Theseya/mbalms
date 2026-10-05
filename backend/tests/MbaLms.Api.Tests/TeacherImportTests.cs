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

public class TeacherImportTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Template_matches_export_columns()
    {
        var manager = await ManagerAsync();
        var template = await manager.GetAsync("/api/manager/imports/teachers/template");
        await template.EnsureStatusAsync(HttpStatusCode.OK);

        using var wb = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        Assert.Equal("Фамилия", wb.Worksheet(1).Cell(1, 1).GetString());
        Assert.Equal("Имя", wb.Worksheet(1).Cell(1, 2).GetString());
        Assert.Equal("Отчество", wb.Worksheet(1).Cell(1, 3).GetString());
        Assert.Equal("Email", wb.Worksheet(1).Cell(1, 4).GetString());
    }

    [Fact]
    public async Task Preview_does_not_write_and_confirm_creates_and_updates_by_email()
    {
        var manager = await ManagerAsync();
        var email = $"{Unique("t")}@test.local";
        var existing = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = "Сидоров", firstName = "Сидор", email }, HttpStatusCode.Created);
        var before = await manager.GetJsonAsync<List<TeacherDto>>("/api/manager/teachers");

        var preview = await PreviewAsync(manager, [
            ("Сидоров", "Сидор", null, email),
            ("Новиков", "Ник", "Николаевич", $"{Unique("new")}@test.local")
        ]);
        Assert.False(string.IsNullOrEmpty(preview.ImportId));
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);

        var afterPreview = await manager.GetJsonAsync<List<TeacherDto>>("/api/manager/teachers");
        Assert.Equal(before.Count, afterPreview.Count);
        Assert.Equal("Сидор", afterPreview.Single(t => t.Id == existing.Id).FirstName);

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        var after = await manager.GetJsonAsync<List<TeacherDto>>("/api/manager/teachers");
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Contains(after, t => t.LastName == "Новиков" && t.MiddleName == "Николаевич");
    }

    [Fact]
    public async Task Update_by_fio_when_email_empty_in_db_can_set_email()
    {
        var manager = await ManagerAsync();
        var existing = await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = "Козлов", firstName = "Коз", middleName = (string?)null, email = (string?)null },
            HttpStatusCode.Created);
        var newEmail = $"{Unique("koz")}@test.local";

        var preview = await PreviewAsync(manager, [("Козлов", "Коз", null, newEmail)]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Update, row.Action);

        await ConfirmAsync(manager, preview.ImportId);
        var updated = await manager.GetJsonAsync<TeacherDto>($"/api/manager/teachers/{existing.Id}");
        Assert.Equal(newEmail, updated.Email);
    }

    [Fact]
    public async Task Fio_match_with_different_nonempty_email_is_conflict()
    {
        var manager = await ManagerAsync();
        await manager.PostJsonAsync<TeacherDto>("/api/manager/teachers",
            new { lastName = "Орлов", firstName = "Олег", email = "oleg@test.local" }, HttpStatusCode.Created);

        var preview = await PreviewAsync(manager, [("Орлов", "Олег", null, "other@test.local")]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Conflict, row.Action);
        Assert.Contains(row.Errors, e => e.Code == FieldCodes.Ambiguous && e.Field == "email");
    }

    [Fact]
    public async Task Multiple_teachers_with_same_email_in_db_are_conflicts()
    {
        var manager = await ManagerAsync();
        var email = $"{Unique("dup")}@test.local";
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            db.Teachers.Add(new Teacher { Id = Guid.CreateVersion7(), LastName = "A", FirstName = "One", Email = email });
            db.Teachers.Add(new Teacher { Id = Guid.CreateVersion7(), LastName = "B", FirstName = "Two", Email = email.ToUpperInvariant() });
            await db.SaveChangesAsync();
        }

        var preview = await PreviewAsync(manager, [("X", "Y", null, email)]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Conflict, row.Action);
        Assert.Contains(row.Errors, e => e.Field == "email");
    }

    [Fact]
    public async Task Duplicate_email_in_file_is_conflict()
    {
        var manager = await ManagerAsync();
        var email = $"{Unique("file")}@test.local";
        var preview = await PreviewAsync(manager, [
            ("A", "B", null, email),
            ("C", "D", null, email.ToUpperInvariant())
        ]);
        Assert.Equal(2, preview.ConflictCount);
        Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Conflict, r.Action));
    }

    [Fact]
    public async Task Duplicate_fio_in_file_is_conflict()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [
            ("Иванов", "Иван", "Иванович", null),
            ("ИВАНОВ", "иван", "Иванович", "a@test.local")
        ]);
        Assert.Equal(2, preview.ConflictCount);
    }

    [Fact]
    public async Task Expired_importId_cannot_be_confirmed()
    {
        var manager = await ManagerAsync();
        var preview = await PreviewAsync(manager, [("Expire", "Me", null, null)]);
        Factory.Services.GetRequiredService<ImportSessionStore>().ExpireForTests(preview.ImportId);

        var res = await manager.PostAsync("/api/manager/imports/teachers/confirm", new { importId = preview.ImportId });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Students_cannot_import_teachers()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var client = await StudentAsync(student);

        var res = await client.GetAsync("/api/manager/imports/teachers/template");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager,
        (string Last, string First, string? Middle, string? Email)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Преподаватели");
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
        content.Add(file, "file", "teachers.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/teachers/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/teachers/confirm", new { importId });
}
