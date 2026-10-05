using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class GradeImportTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Template_has_email_discipline_period_grade_only()
    {
        var manager = await ManagerAsync();
        var template = await manager.GetAsync("/api/manager/imports/grades/template");
        await template.EnsureStatusAsync(HttpStatusCode.OK);
        using var wb = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        var ws = wb.Worksheet(1);
        Assert.Equal("Email", ws.Cell(1, 1).GetString());
        Assert.Equal("Дисциплина", ws.Cell(1, 2).GetString());
        Assert.Equal("Учебный период", ws.Cell(1, 3).GetString());
        Assert.Equal("Оценка", ws.Cell(1, 4).GetString());
        Assert.True(ws.Cell(1, 5).IsEmpty());
    }

    [Fact]
    public async Task Preview_does_not_write_confirm_creates_draft_and_updates_draft()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var existing = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value = 70 },
            HttpStatusCode.Created);

        var otherStudent = await CreateStudentAsync(manager, group.Id, "Новиков");
        var before = await CountGradesAsync();
        var beforeNotes = await CountGradeNotesAsync(student.Id);

        var preview = await PreviewAsync(manager, [
            (student.Email, discipline.Name, period.Name, 85),
            (otherStudent.Email, discipline.Name, period.Name, 90)
        ]);
        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(1, preview.UpdateCount);
        Assert.Equal(before, await CountGradesAsync());

        var result = await ConfirmAsync(manager, preview.ImportId);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        var updated = await manager.GetJsonAsync<GradeDto>($"/api/manager/grades/{existing.Id}");
        Assert.Equal(85, updated.Value);
        Assert.Equal(GradeStatus.Draft, updated.Status);
        Assert.Null(updated.PublishedAt);

        var created = Assert.Single(await manager.GetJsonAsync<List<GradeDto>>("/api/manager/grades"),
            g => g.StudentId == otherStudent.Id);
        Assert.Equal(90, created.Value);
        Assert.Equal(GradeStatus.Draft, created.Status);

        Assert.Equal(beforeNotes, await CountGradeNotesAsync(student.Id));
        Assert.Equal(0, await CountGradeNotesAsync(otherStudent.Id));
    }

    [Fact]
    public async Task Published_grade_is_error_and_not_changed()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        var grade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = student.Id, disciplineId = discipline.Id, periodId = period.Id, value = 60 },
            HttpStatusCode.Created);
        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");

        var preview = await PreviewAsync(manager, [(student.Email, discipline.Name, period.Name, 99)]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Code == ErrorCodes.InvalidStatusTransition);

        Assert.Equal(0, preview.CreateCount + preview.UpdateCount);
        var after = await manager.GetJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}");
        Assert.Equal(60, after.Value);
        Assert.Equal(GradeStatus.Published, after.Status);
    }

    [Fact]
    public async Task Archived_group_student_is_error()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);
        await manager.PostJsonAsync<GroupDto>($"/api/manager/groups/{group.Id}/archive");

        var preview = await PreviewAsync(manager, [(student.Email, discipline.Name, period.Name, 50)]);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportRowAction.Error, row.Action);
        Assert.Contains(row.Errors, e => e.Code == ErrorCodes.GroupArchived);
    }

    [Fact]
    public async Task Duplicate_keys_in_file_and_unknown_period_are_rejected()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);

        var dup = await PreviewAsync(manager, [
            (student.Email, discipline.Name, period.Name, 10),
            (student.Email, discipline.Name, period.Name, 20)
        ]);
        Assert.Equal(2, dup.ConflictCount);

        var missing = await PreviewAsync(manager, [
            (student.Email, discipline.Name, Unique("NoPeriod"), 40)
        ]);
        Assert.Equal(ImportRowAction.Error, Assert.Single(missing.Rows).Action);
        Assert.Contains(missing.Rows[0].Errors, e => e.Field == "period" && e.Code == FieldCodes.NotFound);
    }

    [Fact]
    public async Task Out_of_range_and_formula_are_rejected()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var period = await CreatePeriodAsync(manager);

        var range = await PreviewAsync(manager, [(student.Email, discipline.Name, period.Name, 101)]);
        Assert.Equal(ImportRowAction.Error, Assert.Single(range.Rows).Action);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Оценки");
        ws.Cell(1, 1).Value = "Email";
        ws.Cell(1, 2).Value = "Дисциплина";
        ws.Cell(1, 3).Value = "Учебный период";
        ws.Cell(1, 4).Value = "Оценка";
        ws.Cell(2, 1).Value = student.Email;
        ws.Cell(2, 2).Value = discipline.Name;
        ws.Cell(2, 3).Value = period.Name;
        ws.Cell(2, 4).FormulaA1 = "=50+50";
        var formula = await PreviewFileAsync(manager, wb);
        Assert.Equal(string.Empty, formula.ImportId);
        var err = Assert.Single(formula.FileErrors);
        Assert.Equal(FieldCodes.FormulaNotAllowed, err.Code);
        Assert.Equal("D", err.Column);
    }

    [Fact]
    public async Task Password_column_is_rejected()
    {
        var manager = await ManagerAsync();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Оценки");
        ws.Cell(1, 1).Value = "Email";
        ws.Cell(1, 2).Value = "Дисциплина";
        ws.Cell(1, 3).Value = "Учебный период";
        ws.Cell(1, 4).Value = "Оценка";
        ws.Cell(1, 5).Value = "Password";
        ws.Cell(2, 1).Value = "a@test.local";
        ws.Cell(2, 2).Value = "D";
        ws.Cell(2, 3).Value = "P";
        ws.Cell(2, 4).Value = 10;
        ws.Cell(2, 5).Value = "x";

        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "grades.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/grades/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportPasswordColumn, await res.ErrorCodeAsync());
    }

    [Fact]
    public async Task Students_cannot_import_grades()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var client = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/manager/imports/grades/template")).StatusCode);
    }

    private async Task<int> CountGradesAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Data.AppDbContext>().Grades.CountAsync();
    }

    private async Task<int> CountGradeNotesAsync(Guid studentId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var userId = await db.Students.Where(s => s.Id == studentId).Select(s => s.UserId).SingleAsync();
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.GradePublished);
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
        content.Add(file, "file", "grades.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/grades/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }

    private static async Task<ImportConfirmResultDto> ConfirmAsync(ApiClient manager, string importId) =>
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/grades/confirm", new { importId });
}
