using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MbaLms.Api.Tests;

public class StudentImportChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    private const string StrongPassword = "Student-Import-1";

    [Fact]
    public async Task Students_preview_does_not_log_confirm_logs_create_update_skip()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var other = await CreateGroupAsync(manager);
        var existingLast = Unique("ImpS");
        var noopLast = Unique("ImpSkip");
        var existing = await CreateStudentAsync(manager, group.Id, existingLast);
        var noop = await CreateStudentAsync(manager, group.Id, noopLast);
        var logsBefore = await TotalLogsDb();
        var studentsBefore = await CountStudentsDb();
        var usersBefore = await CountUsersDb();
        var roleLinksBefore = await CountUserRolesDb();
        var existingLogs = await CountLogsDb(existing.Id);
        var noopLogs = await CountLogsDb(noop.Id);

        var newEmail = $"{Unique("new")}@test.local";
        var preview = await PreviewAsync(manager, [
            (existingLast, "NewFirst", null, existing.Email, other.Name),
            (Unique("ImpSNew"), "Nik", "N", newEmail, group.Name),
            (noopLast, "Иван", null, noop.Email, group.Name)
        ]);
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(2, preview.UpdateCount);
        Assert.Equal(logsBefore, await TotalLogsDb());
        Assert.Equal(studentsBefore, await CountStudentsDb());
        Assert.Equal(usersBefore, await CountUsersDb());
        Assert.Equal(roleLinksBefore, await CountUserRolesDb());
        Assert.Null(await FindUserByEmailAsync(newEmail));

        var createRow = preview.Rows.Single(r => r.Action == ImportRowAction.Create);
        var result = await ConfirmAsync(manager, preview.ImportId,
            new[] { new { rowNumber = createRow.RowNumber, password = StrongPassword } });
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
        Assert.DoesNotContain(existing.Email, PayloadBlob(update));
        AssertNoIdentityMaterialInPayload(PayloadBlob(update), await FindUserByEmailAsync(existing.Email));

        var created = await db.Students.AsNoTracking().SingleAsync(s => s.Email == newEmail);
        var createLog = await db.ChangeLogEntries.AsNoTracking().SingleAsync(e => e.EntityId == created.Id);
        Assert.Equal(ChangeLogAction.Created, createLog.Action);
        Assert.Equal("excelImport", ParseSource(createLog));
        Assert.Contains("\"password\"", createLog.ChangesJson);
        Assert.Contains("\"set\"", createLog.ChangesJson);
        Assert.DoesNotContain(newEmail, PayloadBlob(createLog));
        Assert.DoesNotContain(StrongPassword, PayloadBlob(createLog));
        AssertNoIdentityMaterialInPayload(PayloadBlob(createLog), await FindUserByEmailAsync(newEmail));
    }

    [Fact]
    public async Task Students_import_save_fail_after_identity_creates_rolls_back_all_rows()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var existingLast = Unique("BatchS");
        var existing = await CreateStudentAsync(manager, group.Id, existingLast);
        var newEmail = $"{Unique("batchnew")}@test.local";
        var preview = await PreviewAsync(manager, [
            (existingLast, "Changed", null, existing.Email, group.Name),
            (Unique("BatchSNew"), "New", null, newEmail, group.Name)
        ]);
        var createRow = preview.Rows.Single(r => r.Action == ImportRowAction.Create);
        var logsBefore = await TotalLogsDb();
        var studentsBefore = await CountStudentsDb();
        var usersBefore = await CountUsersDb();
        var roleLinksBefore = await CountUserRolesDb();
        var existingLogs = await CountLogsDb(existing.Id);
        var existingEmail = existing.Email;
        var existingFirstName = (await NewDb().Students.AsNoTracking().SingleAsync(s => s.Id == existing.Id)).FirstName;

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PostAsync("/api/manager/imports/students/confirm", new
            {
                importId = preview.ImportId,
                passwords = new[] { new { rowNumber = createRow.RowNumber, password = StrongPassword } }
            });
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawSuccessfulIntermediateSaveChanges);
        Assert.True(Factory.SaveFail.SawIdentityUserInsert);
        Assert.True(Factory.SaveFail.SawIdentityRoleAssign);
        Assert.Equal(newEmail, Factory.SaveFail.CapturedMidTxUserEmail);
        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.Equal(studentsBefore, await db.Students.CountAsync());
        Assert.False(await db.Students.AnyAsync(s => s.Email == newEmail));
        var unchanged = await db.Students.AsNoTracking().SingleAsync(s => s.Id == existing.Id);
        Assert.Equal(existingFirstName, unchanged.FirstName);
        Assert.Equal(existingEmail, unchanged.Email);
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
        Assert.Equal(existingLogs, await db.ChangeLogEntries.CountAsync(e => e.EntityId == existing.Id));
        Assert.Equal(usersBefore, await CountUsersDb());
        Assert.Equal(roleLinksBefore, await CountUserRolesDb());
        Assert.Null(await FindUserByEmailAsync(newEmail));
        Assert.Equal(0, await CountRoleLinksForEmailAsync(newEmail));
        Assert.NotNull(await FindUserByEmailAsync(existingEmail));
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

    private async Task<int> CountStudentsDb()
    {
        await using var db = NewDb();
        return await db.Students.CountAsync();
    }

    private async Task<int> CountUsersDb()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.Users.CountAsync();
    }

    private async Task<AppUser?> FindUserByEmailAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.FindByEmailAsync(email);
    }

    private async Task<int> CountUserRolesDb()
    {
        await using var db = NewDb();
        return await db.Set<IdentityUserRole<Guid>>().CountAsync();
    }

    private async Task<int> CountRoleLinksForEmailAsync(string email)
    {
        await using var db = NewDb();
        var normalized = email.ToUpperInvariant();
        return await (
            from ur in db.Set<IdentityUserRole<Guid>>()
            join u in db.Users on ur.UserId equals u.Id
            where u.NormalizedEmail == normalized
            select ur).CountAsync();
    }

    private static string PayloadBlob(ChangeLogEntry e) => $"{e.ChangesJson}\n{e.ContextJson}";

    /// <summary>Asserts CL JSON never contains Identity secret material (values compared, not logged).</summary>
    private static void AssertNoIdentityMaterialInPayload(string blob, AppUser? user)
    {
        Assert.DoesNotContain("passwordHash", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("concurrencyStamp", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authenticatorKey", blob, StringComparison.OrdinalIgnoreCase);
        if (user is null) return;
        if (!string.IsNullOrEmpty(user.PasswordHash))
            Assert.DoesNotContain(user.PasswordHash, blob);
        if (!string.IsNullOrEmpty(user.SecurityStamp))
            Assert.DoesNotContain(user.SecurityStamp, blob);
        if (!string.IsNullOrEmpty(user.ConcurrencyStamp))
            Assert.DoesNotContain(user.ConcurrencyStamp, blob);
    }

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
        (string Last, string First, string? Middle, string Email, string Group)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Students");
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
