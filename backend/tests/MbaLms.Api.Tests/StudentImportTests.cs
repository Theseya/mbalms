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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class StudentImportTests(ApiFactory factory) : TestBase(factory)
{
    private const string StrongPassword = "Student-Pass-1";

    [Fact]
    public async Task Template_has_no_password_column()
    {
        var manager = await ManagerAsync();
        var template = await manager.GetAsync("/api/manager/imports/students/template");
        await template.EnsureStatusAsync(HttpStatusCode.OK);

        using var wb = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(1);
        Assert.Equal("Фамилия", ws.Cell(1, 1).GetString());
        Assert.Equal("Имя", ws.Cell(1, 2).GetString());
        Assert.Equal("Отчество", ws.Cell(1, 3).GetString());
        Assert.Equal("Email", ws.Cell(1, 4).GetString());
        Assert.Equal("Группа", ws.Cell(1, 5).GetString());
        Assert.True(ws.Cell(1, 6).IsEmpty());
    }

    [Fact]
    public async Task Confirm_creates_n_students_atomically_with_passwords()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var rows = Enumerable.Range(1, 3).Select(i =>
            ($"Иванов{i}", "Иван", (string?)null, $"{Unique($"s{i}")}@test.local", group.Name)).ToArray();

        var preview = await PreviewAsync(manager, rows);
        Assert.Equal(3, preview.CreateCount);
        Assert.Equal(0, preview.UpdateCount);

        var before = await CountStudentsAsync();
        var passwords = preview.Rows.Where(r => r.Action == ImportRowAction.Create)
            .Select(r => new { rowNumber = r.RowNumber, password = StrongPassword }).ToArray();
        var result = await ConfirmAsync(manager, preview.ImportId, passwords);
        Assert.Equal(3, result.Created);

        Assert.Equal(before + 3, await CountStudentsAsync());
        foreach (var (_, _, _, email, _) in rows)
        {
            var client = NewClient();
            await client.LoginAsync(email, StrongPassword);
            (await client.GetAsync("/api/student/dashboard")).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Missing_password_cancels_entire_import()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var emailA = $"{Unique("a")}@test.local";
        var emailB = $"{Unique("b")}@test.local";
        var preview = await PreviewAsync(manager, [
            ("A", "One", null, emailA, group.Name),
            ("B", "Two", null, emailB, group.Name)
        ]);
        var createRows = preview.Rows.Where(r => r.Action == ImportRowAction.Create).OrderBy(r => r.RowNumber).ToList();
        Assert.Equal(2, createRows.Count);

        var res = await manager.PostAsync("/api/manager/imports/students/confirm", new
        {
            importId = preview.ImportId,
            passwords = new[] { new { rowNumber = createRows[0].RowNumber, password = StrongPassword } }
        });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);

        Assert.Empty(await FindStudentsByEmailAsync(emailA));
        Assert.Empty(await FindStudentsByEmailAsync(emailB));
        Assert.False(await UserExistsAsync(emailA));
        Assert.False(await UserExistsAsync(emailB));
    }

    [Fact]
    public async Task Weak_password_rolls_back_without_partial_users()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var emails = new[] { $"{Unique("w1")}@test.local", $"{Unique("w2")}@test.local" };
        var preview = await PreviewAsync(manager, [
            ("W", "One", null, emails[0], group.Name),
            ("W", "Two", null, emails[1], group.Name)
        ]);
        var createRows = preview.Rows.Where(r => r.Action == ImportRowAction.Create).OrderBy(r => r.RowNumber).ToList();

        var res = await manager.PostAsync("/api/manager/imports/students/confirm", new
        {
            importId = preview.ImportId,
            passwords = new[]
            {
                new { rowNumber = createRows[0].RowNumber, password = StrongPassword },
                new { rowNumber = createRows[1].RowNumber, password = "weak" }
            }
        });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains(FieldCodes.PasswordWeak, await res.Content.ReadAsStringAsync());

        foreach (var email in emails)
        {
            Assert.Empty(await FindStudentsByEmailAsync(email));
            Assert.False(await UserExistsAsync(email));
        }
    }

    [Fact]
    public async Task Update_changes_fio_and_group_but_not_password()
    {
        var manager = await ManagerAsync();
        var groupA = await CreateGroupAsync(manager);
        var groupB = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, groupA.Id, "Старый");
        const string originalPassword = "Student-Pass-1";

        var preview = await PreviewAsync(manager, [
            ("Новый", "Имя", "Отч", account.Email, groupB.Name)
        ]);
        Assert.Equal(1, preview.UpdateCount);
        Assert.Equal(0, preview.CreateCount);

        var result = await ConfirmAsync(manager, preview.ImportId, Array.Empty<object>());
        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);

        var updated = Assert.Single(await FindStudentsByEmailAsync(account.Email));
        Assert.Equal("Новый", updated.LastName);
        Assert.Equal("Имя", updated.FirstName);
        Assert.Equal("Отч", updated.MiddleName);
        Assert.Equal(groupB.Id, updated.GroupId);

        var client = NewClient();
        await client.LoginAsync(account.Email, originalPassword);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await NewClient().TryLoginAsync(account.Email, "Other-Pass-99")).StatusCode);
    }

    [Fact]
    public async Task Password_for_update_row_is_rejected()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var preview = await PreviewAsync(manager, [
            ("X", "Y", null, account.Email, group.Name)
        ]);
        Assert.Equal(ImportRowAction.Update, Assert.Single(preview.Rows).Action);

        var res = await manager.PostAsync("/api/manager/imports/students/confirm", new
        {
            importId = preview.ImportId,
            passwords = new[] { new { rowNumber = preview.Rows[0].RowNumber, password = StrongPassword } }
        });
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Password_column_in_file_is_rejected()
    {
        var manager = await ManagerAsync();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Студенты");
        ws.Cell(1, 1).Value = "Фамилия";
        ws.Cell(1, 2).Value = "Имя";
        ws.Cell(1, 3).Value = "Отчество";
        ws.Cell(1, 4).Value = "Email";
        ws.Cell(1, 5).Value = "Группа";
        ws.Cell(1, 6).Value = "Пароль";
        ws.Cell(2, 1).Value = "Тест";
        ws.Cell(2, 2).Value = "Студент";
        ws.Cell(2, 4).Value = $"{Unique("p")}@test.local";
        ws.Cell(2, 5).Value = "G";
        ws.Cell(2, 6).Value = "Secret-Pass-1";

        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "students.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/students/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportPasswordColumn, await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Duplicate_emails_in_file_are_conflicts()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("dup")}@test.local";
        var preview = await PreviewAsync(manager, [
            ("A", "One", null, email, group.Name),
            ("B", "Two", null, email.ToUpperInvariant(), group.Name)
        ]);
        Assert.Equal(2, preview.ConflictCount);
        Assert.All(preview.Rows, r => Assert.Equal(ImportRowAction.Conflict, r.Action));
    }

    [Fact]
    public async Task Archived_group_is_error()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/archive");

        var preview = await PreviewAsync(manager, [
            ("A", "One", null, $"{Unique("arch")}@test.local", group.Name)
        ]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Code == ErrorCodes.GroupArchived);
    }

    [Fact]
    public async Task Manager_email_is_not_converted_to_student()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var preview = await PreviewAsync(manager, [
            ("Mgr", "User", null, ApiFactory.ManagerEmail, group.Name)
        ]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Conflict, row.Action);
        Assert.Contains(row.Errors, e => e.Field == "email");
    }

    [Fact]
    public async Task Students_cannot_import_students()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var client = await StudentAsync(student);

        var res = await client.GetAsync("/api/manager/imports/students/template");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Preview_does_not_write_to_database()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var email = $"{Unique("prev")}@test.local";
        var before = await CountStudentsAsync();
        var preview = await PreviewAsync(manager, [("Prev", "View", null, email, group.Name)]);
        Assert.False(string.IsNullOrEmpty(preview.ImportId));
        Assert.Equal(before, await CountStudentsAsync());
        Assert.False(await UserExistsAsync(email));
    }

    private async Task<int> CountStudentsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        return await db.Students.CountAsync();
    }

    private async Task<List<Student>> FindStudentsByEmailAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        return await db.Students.Where(s => s.Email.ToLower() == email.ToLower()).ToListAsync();
    }

    private async Task<bool> UserExistsAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.FindByEmailAsync(email) is not null;
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager,
        (string Last, string First, string? Middle, string Email, string Group)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Студенты");
        ws.Cell(1, 1).Value = "Фамилия";
        ws.Cell(1, 2).Value = "Имя";
        ws.Cell(1, 3).Value = "Отчество";
        ws.Cell(1, 4).Value = "Email";
        ws.Cell(1, 5).Value = "Группа";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Last;
            ws.Cell(i + 2, 2).Value = rows[i].First;
            if (rows[i].Middle is not null) ws.Cell(i + 2, 3).Value = rows[i].Middle;
            ws.Cell(i + 2, 4).Value = rows[i].Email;
            ws.Cell(i + 2, 5).Value = rows[i].Group;
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
        content.Add(file, "file", "students.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/students/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId, object passwords) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/students/confirm",
            new { importId, passwords });
}
