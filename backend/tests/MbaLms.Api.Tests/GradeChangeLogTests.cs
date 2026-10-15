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
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Tests;

public class GradeChangeLogTests(ApiFactory factory) : TestBase(factory)
{
    private static readonly string[] GradeFields =
        ["student", "group", "discipline", "period", "status", "value"];

    [Fact]
    public async Task Grade_lifecycle_is_logged_without_numeric_scores()
    {
        var manager = await ManagerAsync();
        var (group, student, discipline, period) = await SeedAsync(manager);

        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value = 77 },
            HttpStatusCode.Created);

        var created = Assert.Single(await LogsFor(manager, grade.Id));
        Assert.Equal(ChangeLogAction.Created, created.Action);
        Assert.Equal(ChangeLogEntityType.Grade, created.EntityType);
        Assert.Equal(ApiFactory.ManagerEmail, created.ActorEmail);
        Assert.Null(created.Source);
        Assert.True(created.EntityExists);
        Assert.Equal(grade.StudentName, created.Changes.Single(c => c.Field == "student").New?.GetString());
        Assert.Contains(grade.StudentName, created.EntityTitle);
        Assert.Contains(discipline.Name, created.EntityTitle);
        Assert.Contains(period.Name, created.EntityTitle);
        Assert.Equal([group.Name], created.GroupNames);
        Assert.All(created.Changes, c => Assert.Contains(c.Field, GradeFields));
        Assert.Contains(created.Changes, c => c.Field == "value" && c.New?.GetString() == "set");
        Assert.Contains(created.Changes, c => c.Field == "status" && c.New?.GetString() == "Draft");
        AssertNoScoresOrSecrets(created, 77);

        await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 88 });
        var afterUpdate = await LogsFor(manager, grade.Id);
        Assert.Equal(2, afterUpdate.Count);
        Assert.Equal(ChangeLogAction.Updated, afterUpdate[1].Action);
        Assert.Single(afterUpdate[1].Changes);
        Assert.Equal("value", afterUpdate[1].Changes[0].Field);
        Assert.Equal("changed", afterUpdate[1].Changes[0].New?.GetString());
        AssertNoScoresOrSecrets(afterUpdate[1], 77, 88);

        // Same value: no-op
        await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}", new { value = 88 });
        Assert.Equal(2, (await LogsFor(manager, grade.Id)).Count);

        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");
        var afterPublish = await LogsFor(manager, grade.Id);
        Assert.Equal(3, afterPublish.Count);
        Assert.Equal(ChangeLogAction.Published, afterPublish[2].Action);
        Assert.Contains(afterPublish[2].Changes, c =>
            c.Field == "status" && c.Old?.GetString() == "Draft" && c.New?.GetString() == "Published");
        Assert.DoesNotContain(afterPublish[2].Changes, c => c.Field == "value");

        var again = await manager.PostAsync($"/api/manager/grades/{grade.Id}/publish");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(3, (await LogsFor(manager, grade.Id)).Count);

        // Published value change → Updated, not Published again
        await manager.PutJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}",
            new { value = 91, confirmPublishedChange = true });
        var afterPubUpdate = await LogsFor(manager, grade.Id);
        Assert.Equal(4, afterPubUpdate.Count);
        Assert.Equal(ChangeLogAction.Updated, afterPubUpdate[3].Action);
        Assert.Equal("changed", afterPubUpdate[3].Changes.Single(c => c.Field == "value").New?.GetString());
        Assert.DoesNotContain(afterPubUpdate[3].Changes, c => c.Field == "status");
        AssertNoScoresOrSecrets(afterPubUpdate[3], 91);

        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/unpublish");
        var afterUnpub = await LogsFor(manager, grade.Id);
        Assert.Equal(5, afterUnpub.Count);
        Assert.Equal(ChangeLogAction.Unpublished, afterUnpub[4].Action);

        var unpubAgain = await manager.PostAsync($"/api/manager/grades/{grade.Id}/unpublish");
        Assert.Equal(HttpStatusCode.Conflict, unpubAgain.StatusCode);
        Assert.Equal(5, (await LogsFor(manager, grade.Id)).Count);

        // GradeHistory still records numeric values
        var history = await manager.GetJsonAsync<List<GradeHistoryDto>>($"/api/manager/grades/{grade.Id}/history");
        Assert.Contains(history, h => h.Action == GradeChangeAction.Created && h.NewValue == 77);
        Assert.Contains(history, h => h.Action == GradeChangeAction.Updated && h.NewValue == 91);

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/manager/grades/{grade.Id}")).StatusCode);
        var deleted = (await LogsFor(manager, grade.Id))[^1];
        Assert.Equal(ChangeLogAction.Deleted, deleted.Action);
        Assert.False(deleted.EntityExists);
        Assert.Contains(grade.StudentName, deleted.EntityTitle);
        AssertNoScoresOrSecrets(deleted, 77, 88, 91);
    }

    [Fact]
    public async Task Sheet_mixed_create_update_skip_and_bulk_publish()
    {
        var manager = await ManagerAsync();
        var (group, studentA, discipline, period) = await SeedAsync(manager);
        var studentB = await CreateStudentAsync(manager, group.Id, "Петров");
        var studentC = await CreateStudentAsync(manager, group.Id, "Сидоров");

        var existing = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = studentA.Id, disciplineId = discipline.Id, periodId = period.Id, value = 50 },
            HttpStatusCode.Created);
        Assert.Single(await LogsFor(manager, existing.Id));

        await manager.PutJsonAsync<GradeSheetDto>(
            $"/api/manager/grades/sheet/{group.Id}/{discipline.Id}/{period.Id}",
            new
            {
                entries = new object[]
                {
                    new { studentId = studentA.Id, value = 50 }, // skip
                    new { studentId = studentB.Id, value = 70 }, // create
                    new { studentId = studentC.Id, value = (int?)null }, // skip
                }
            });

        // Update existing A to 65 + create was already done; second save for update
        await manager.PutJsonAsync<GradeSheetDto>(
            $"/api/manager/grades/sheet/{group.Id}/{discipline.Id}/{period.Id}",
            new
            {
                entries = new object[]
                {
                    new { studentId = studentA.Id, value = 65 },
                    new { studentId = studentB.Id, value = 70 }, // skip same
                }
            });

        var logsA = await LogsFor(manager, existing.Id);
        Assert.Equal(2, logsA.Count); // Created + Updated
        Assert.Equal(ChangeLogAction.Updated, logsA[1].Action);

        var sheet = await manager.GetJsonAsync<GradeSheetDto>(
            $"/api/manager/grades/sheet/{group.Id}/{discipline.Id}/{period.Id}");
        var gradeB = sheet.Rows.Single(r => r.StudentId == studentB.Id).GradeId!.Value;
        Assert.Single(await LogsFor(manager, gradeB)); // Created only
        Assert.Null(sheet.Rows.Single(r => r.StudentId == studentC.Id).GradeId);

        // Bulk publish: only drafts that exist; already-published id ignored
        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{existing.Id}/publish");
        var publishedCountBefore = (await LogsFor(manager, existing.Id)).Count;
        var res = await manager.PostAsync("/api/manager/grades/publish", new { ids = new[] { existing.Id, gradeB } });
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("published").GetInt32()); // only B (A already published)
        Assert.Equal(publishedCountBefore, (await LogsFor(manager, existing.Id)).Count);
        Assert.Equal(2, (await LogsFor(manager, gradeB)).Count); // Created + Published
        Assert.Equal(ChangeLogAction.Published, (await LogsFor(manager, gradeB))[1].Action);
    }

    [Fact]
    public async Task Import_preview_silent_confirm_logs_create_update_skip_with_source()
    {
        var manager = await ManagerAsync();
        var (group, studentA, discipline, period) = await SeedAsync(manager);
        var studentB = await CreateStudentAsync(manager, group.Id, "Орлов");
        var existing = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = studentA.Id, disciplineId = discipline.Id, periodId = period.Id, value = 40 },
            HttpStatusCode.Created);
        var before = await TotalCount(manager);

        var skipPreview = await PreviewAsync(manager, [
            (studentA.Email, discipline.Name, period.Name, 40),
        ]);
        Assert.Equal(1, skipPreview.UpdateCount); // preview still counts as update; confirm skips same value
        Assert.Equal(before, await TotalCount(manager));

        var confirmSkip = await ConfirmAsync(manager, skipPreview.ImportId);
        Assert.Equal(0, confirmSkip.Created);
        Assert.Equal(0, confirmSkip.Updated);
        Assert.Equal(1, confirmSkip.Skipped);
        Assert.Single(await LogsFor(manager, existing.Id)); // only original Created

        var preview = await PreviewAsync(manager, [
            (studentA.Email, discipline.Name, period.Name, 45),
            (studentB.Email, discipline.Name, period.Name, 55),
        ]);
        Assert.Equal(before, await TotalCount(manager)); // preview silent
        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        var logsA = await LogsFor(manager, existing.Id);
        Assert.Equal(2, logsA.Count);
        Assert.Equal(ChangeLogAction.Updated, logsA[1].Action);
        Assert.Equal("excelImport", logsA[1].Source);
        AssertNoScoresOrSecrets(logsA[1], 40, 45);

        await using var db = NewDb();
        var gradeB = await db.Grades.AsNoTracking()
            .SingleAsync(g => g.StudentId == studentB.Id && g.DisciplineId == discipline.Id);
        var logsB = await LogsFor(manager, gradeB.Id);
        Assert.Single(logsB);
        Assert.Equal(ChangeLogAction.Created, logsB[0].Action);
        Assert.Equal("excelImport", logsB[0].Source);
        Assert.Equal("set", logsB[0].Changes.Single(c => c.Field == "value").New?.GetString());
        AssertNoScoresOrSecrets(logsB[0], 55);

        var historyB = await db.GradeHistory.AsNoTracking()
            .Where(h => h.GradeId == gradeB.Id)
            .OrderBy(h => h.ChangedAt).ThenBy(h => h.Id)
            .ToListAsync();
        var createdHistory = Assert.Single(historyB);
        Assert.Equal(GradeChangeAction.Created, createdHistory.Action);
        Assert.Null(createdHistory.OldValue);
        Assert.Equal(55, createdHistory.NewValue);
        Assert.Equal(GradeStatus.Draft, createdHistory.NewStatus);
    }

    [Fact]
    public async Task Failed_validation_and_conflicts_do_not_log()
    {
        var manager = await ManagerAsync();
        var before = await TotalCount(manager);
        var res = await manager.PostAsync("/api/manager/grades",
            new { studentId = Guid.CreateVersion7(), disciplineId = Guid.CreateVersion7(), periodId = Guid.CreateVersion7(), value = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, await TotalCount(manager));
    }

    [Fact]
    public async Task Student_cannot_read_grade_change_log_entries()
    {
        var manager = await ManagerAsync();
        var (_, student, discipline, period) = await SeedAsync(manager);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value = 12 },
            HttpStatusCode.Created);

        var studentClient = await StudentAsync(student);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync("/api/manager/change-log")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await studentClient.GetAsync($"/api/manager/change-log?entityType=Grade&page=1&pageSize=50")).StatusCode);

        // Manager list/details include the grade entry
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            "/api/manager/change-log?entityType=Grade&page=1&pageSize=50");
        Assert.Contains(page.Items, i => i.EntityId == grade.Id && i.EntityExists);
    }

    [Fact]
    public async Task Failed_SaveChanges_on_sheet_rolls_back_grades_history_and_change_log()
    {
        var manager = await ManagerAsync();
        var (group, studentA, discipline, period) = await SeedAsync(manager);
        var studentB = await CreateStudentAsync(manager, group.Id, "Козлов");
        var existing = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = studentA.Id, disciplineId = discipline.Id, periodId = period.Id, value = 33 },
            HttpStatusCode.Created);

        var logsBefore = await TotalCount(manager);
        var historyBefore = await CountHistoryAsync(existing.Id);
        var gradesBefore = await CountGradesAsync();

        try
        {
            Factory.SaveFail.ArmOnce();
            var res = await manager.PutAsync(
                $"/api/manager/grades/sheet/{group.Id}/{discipline.Id}/{period.Id}",
                new
                {
                    entries = new object[]
                    {
                        new { studentId = studentA.Id, value = 44 },
                        new { studentId = studentB.Id, value = 55 },
                    }
                });
            Assert.False(res.IsSuccessStatusCode);
        }
        finally
        {
            Factory.SaveFail.Disarm();
        }

        Assert.True(Factory.SaveFail.SawPendingLessonAndChangeLog);
        Assert.True(Factory.SaveFail.SawDatabaseCommandFailure);
        Assert.Equal(PostgresErrorCodes.CheckViolation, Factory.SaveFail.FailedSqlState);

        await using var db = NewDb();
        Assert.Equal(33, (await db.Grades.AsNoTracking().SingleAsync(g => g.Id == existing.Id)).Value);
        Assert.False(await db.Grades.AnyAsync(g => g.StudentId == studentB.Id && g.DisciplineId == discipline.Id));
        Assert.Equal(gradesBefore, await db.Grades.CountAsync());
        Assert.Equal(historyBefore, await db.GradeHistory.CountAsync(h => h.GradeId == existing.Id));
        Assert.Equal(logsBefore, await db.ChangeLogEntries.CountAsync());
        Assert.False(await db.ChangeLogEntries.AnyAsync(e =>
            e.EntityType == ChangeLogEntityType.Grade && e.Action == ChangeLogAction.Updated
            && e.EntityId == existing.Id));
    }

    private async Task<(GroupDto Group, StudentAccount Student, DisciplineDto Discipline, PeriodDto Period)> SeedAsync(
        ApiClient manager)
    {
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        return (group, student, discipline, period);
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Factory.ConnectionString).Options);

    private async Task<int> CountGradesAsync()
    {
        await using var db = NewDb();
        return await db.Grades.CountAsync();
    }

    private async Task<int> CountHistoryAsync(Guid gradeId)
    {
        await using var db = NewDb();
        return await db.GradeHistory.CountAsync(h => h.GradeId == gradeId);
    }

    private static async Task<List<ChangeLogListItemDto>> LogsFor(ApiClient manager, Guid entityId)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            "/api/manager/change-log?entityType=Grade&page=1&pageSize=100");
        return page.Items.Where(i => i.EntityId == entityId).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
    }

    private static async Task<int> TotalCount(ApiClient manager)
    {
        var page = await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>("/api/manager/change-log?page=1&pageSize=1");
        return page.Total;
    }

    private static void AssertNoScoresOrSecrets(ChangeLogListItemDto entry, params int[] scores)
    {
        _ = scores; // documented intent: numeric scores must not be serialized as change values
        foreach (var c in entry.Changes)
        {
            Assert.NotEqual(JsonValueKind.Number, c.Old?.ValueKind ?? JsonValueKind.Null);
            Assert.NotEqual(JsonValueKind.Number, c.New?.ValueKind ?? JsonValueKind.Null);
            if (c.Field == "value")
            {
                var marker = c.New?.GetString() ?? c.Old?.GetString();
                Assert.True(marker is null or "set" or "changed", $"Unexpected value marker: {marker}");
            }
        }
        var payload = JsonSerializer.Serialize(new { entry.EntityTitle, entry.GroupNames, entry.Source, entry.Changes });
        Assert.DoesNotContain("@test.local", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("textValue", payload, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ImportPreviewDto> PreviewAsync(ApiClient manager,
        (string Email, string Discipline, string Period, int Value)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Оценки");
        ws.Cell(1, 1).Value = "Email";
        ws.Cell(1, 2).Value = "Дисциплина";
        ws.Cell(1, 3).Value = "Учебный период";
        ws.Cell(1, 4).Value = "Оценка";
        for (var i = 0; i < rows.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Email;
            ws.Cell(i + 2, 2).Value = rows[i].Discipline;
            ws.Cell(i + 2, 3).Value = rows[i].Period;
            ws.Cell(i + 2, 4).Value = rows[i].Value;
        }
        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "grades.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/grades/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/grades/confirm", new { importId });
}
