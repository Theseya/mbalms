using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Data;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MbaLms.Api.Tests;

public class DisciplineProgramTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Manager_uploads_views_replaces_and_deletes_the_programme()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager);
        var first = Pdf("first");

        var uploaded = await UploadAsync(manager, discipline.Id, first, "ПУД Финансы.pdf");
        Assert.Equal("ПУД Финансы.pdf", uploaded.FileName);
        Assert.Equal(first.Length, uploaded.SizeBytes);
        var listed = (await manager.GetJsonAsync<List<DisciplineDto>>("/api/manager/disciplines")).Single(d => d.Id == discipline.Id);
        Assert.Equal("ПУД Финансы.pdf", listed.Program?.FileName);

        var view = await manager.GetAsync($"/api/manager/disciplines/{discipline.Id}/program");
        await view.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("application/pdf", view.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", view.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("ПУД Финансы.pdf", view.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(first, await view.Content.ReadAsByteArrayAsync());
        var firstKey = await StorageKeyAsync(discipline.Id);
        Assert.True(File.Exists(StoredFile(firstKey!.Value)));

        var second = Pdf("second");
        var replaced = await UploadAsync(manager, discipline.Id, second, "new.pdf");
        Assert.Equal("new.pdf", replaced.FileName);
        Assert.Equal(second, await (await manager.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).Content.ReadAsByteArrayAsync());
        var secondKey = await StorageKeyAsync(discipline.Id);
        Assert.NotEqual(firstKey, secondKey);
        Assert.False(File.Exists(StoredFile(firstKey.Value)), "replaced file must not be left behind");
        Assert.True(File.Exists(StoredFile(secondKey!.Value)));

        await (await manager.DeleteAsync($"/api/manager/disciplines/{discipline.Id}/program")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).StatusCode);
        Assert.Null(await StorageKeyAsync(discipline.Id));
        Assert.False(File.Exists(StoredFile(secondKey.Value)));
        Assert.Null((await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{discipline.Id}")).Program);
    }

    [Fact]
    public async Task Discipline_without_programme_has_an_empty_state()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager, "без ПУД");

        var dto = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{discipline.Id}");
        Assert.Null(dto.Program);
        Assert.Equal("без ПУД", dto.Description);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.DeleteAsync($"/api/manager/disciplines/{discipline.Id}/program")).StatusCode);
        Assert.Equal(ApiFactory.MaxProgramPdfBytes,
            (await manager.GetJsonAsync<DisciplineProgramLimitsDto>("/api/manager/disciplines/program-limits")).MaxBytes);
    }

    [Fact]
    public async Task Unsuitable_files_are_rejected_and_nothing_is_stored()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager);
        var filesBefore = StoredFileCount();

        await AssertRejectedAsync(manager, discipline.Id, Encoding.UTF8.GetBytes("просто текст, не PDF"), "fake.pdf",
            HttpStatusCode.BadRequest, ErrorCodes.FileNotPdf);
        await AssertRejectedAsync(manager, discipline.Id, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6], "image.pdf",
            HttpStatusCode.BadRequest, ErrorCodes.FileNotPdf);
        await AssertRejectedAsync(manager, discipline.Id, Pdf("ok"), "document.docx",
            HttpStatusCode.BadRequest, ErrorCodes.FileNotPdf);
        await AssertRejectedAsync(manager, discipline.Id, Encoding.ASCII.GetBytes("%PDF-1.7 truncated without trailer"), "cut.pdf",
            HttpStatusCode.BadRequest, ErrorCodes.FileNotPdf);
        await AssertRejectedAsync(manager, discipline.Id, [], "empty.pdf",
            HttpStatusCode.BadRequest, ErrorCodes.FileRequired);
        await AssertRejectedAsync(manager, discipline.Id, Pdf("big", (int)ApiFactory.MaxProgramPdfBytes + 1), "big.pdf",
            HttpStatusCode.RequestEntityTooLarge, ErrorCodes.FileTooLarge);

        var noFile = await manager.Http.PostAsync($"/api/manager/disciplines/{discipline.Id}/program", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.Equal(ErrorCodes.FileRequired, await noFile.ErrorCodeAsync());

        Assert.Null(await StorageKeyAsync(discipline.Id));
        Assert.Equal(filesBefore, StoredFileCount());
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadRawAsync(manager, Guid.NewGuid(), Pdf("x"), "x.pdf")).StatusCode);
    }

    [Fact]
    public async Task Failed_replacement_keeps_the_previous_programme()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager);
        var original = Pdf("original");
        await UploadAsync(manager, discipline.Id, original, "original.pdf");
        var key = await StorageKeyAsync(discipline.Id);

        await AssertRejectedAsync(manager, discipline.Id, Encoding.UTF8.GetBytes("<html>not a pdf</html>"), "page.pdf",
            HttpStatusCode.BadRequest, ErrorCodes.FileNotPdf);
        await AssertRejectedAsync(manager, discipline.Id, Pdf("big", (int)ApiFactory.MaxProgramPdfBytes + 10), "big.pdf",
            HttpStatusCode.RequestEntityTooLarge, ErrorCodes.FileTooLarge);

        Assert.Equal(key, await StorageKeyAsync(discipline.Id));
        var dto = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{discipline.Id}");
        Assert.Equal("original.pdf", dto.Program?.FileName);
        Assert.Equal(original, await (await manager.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Student_opens_only_programmes_of_own_schedule_or_published_grades()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var otherGroup = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var otherAccount = await CreateStudentAsync(manager, otherGroup.Id);
        var teacher = await CreateTeacherAsync(manager);
        var period = await CreatePeriodAsync(manager);

        var scheduled = await CreateDisciplineAsync(manager);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", new
        {
            groupId = group.Id, disciplineId = scheduled.Id, teacherId = teacher.Id,
            startsAt = "2030-04-01T10:00", endsAt = "2030-04-01T11:30"
        }, HttpStatusCode.Created);
        var graded = await CreateDisciplineAsync(manager);
        var gradedGrade = await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = account.Id, disciplineId = graded.Id, periodId = period.Id, value = 90 }, HttpStatusCode.Created);
        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{gradedGrade.Id}/publish");
        var draftOnly = await CreateDisciplineAsync(manager);
        await manager.PostJsonAsync<GradeDto>("/api/manager/grades",
            new { studentId = account.Id, disciplineId = draftOnly.Id, periodId = period.Id, value = 70 }, HttpStatusCode.Created);
        var unrelated = await CreateDisciplineAsync(manager);
        var withoutProgramme = await CreateDisciplineAsync(manager);
        await manager.PostJsonAsync<LessonDto>("/api/manager/lessons", new
        {
            groupId = group.Id, disciplineId = withoutProgramme.Id, teacherId = teacher.Id,
            startsAt = "2030-04-02T10:00", endsAt = "2030-04-02T11:30"
        }, HttpStatusCode.Created);

        var scheduledPdf = Pdf("scheduled");
        await UploadAsync(manager, scheduled.Id, scheduledPdf, "scheduled.pdf");
        await UploadAsync(manager, graded.Id, Pdf("graded"), "graded.pdf");
        await UploadAsync(manager, draftOnly.Id, Pdf("draft"), "draft.pdf");
        await UploadAsync(manager, unrelated.Id, Pdf("unrelated"), "unrelated.pdf");

        var student = await StudentAsync(account);
        var programs = await student.GetJsonAsync<List<StudentProgramDto>>("/api/student/programs");
        Assert.Equal(new[] { scheduled.Id, graded.Id }.Order(), programs.Select(p => p.DisciplineId).Order());

        var view = await student.GetAsync($"/api/student/disciplines/{scheduled.Id}/program");
        await view.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal("application/pdf", view.Content.Headers.ContentType?.MediaType);
        Assert.Equal(scheduledPdf, await view.Content.ReadAsByteArrayAsync());
        await (await student.GetAsync($"/api/student/disciplines/{graded.Id}/program")).EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/disciplines/{draftOnly.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/disciplines/{unrelated.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/student/disciplines/{withoutProgramme.Id}/program")).StatusCode);

        var schedule = await student.GetJsonAsync<List<StudentLessonDto>>("/api/student/schedule");
        Assert.Contains(schedule, l => l.DisciplineId == scheduled.Id);
        Assert.Contains(await student.GetJsonAsync<List<StudentGradeDto>>("/api/student/grades"), g => g.DisciplineId == graded.Id);

        var other = await StudentAsync(otherAccount);
        Assert.Empty(await other.GetJsonAsync<List<StudentProgramDto>>("/api/student/programs"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/student/disciplines/{scheduled.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/student/disciplines/{graded.Id}/program")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_and_students_cannot_change_or_read_manager_programmes()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var account = await CreateStudentAsync(manager, group.Id);
        var discipline = await CreateDisciplineAsync(manager);
        var original = Pdf("protected");
        await UploadAsync(manager, discipline.Id, original, "protected.pdf");

        var anonymous = await AnonymousAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/student/disciplines/{discipline.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadRawAsync(anonymous, discipline.Id, Pdf("x"), "x.pdf")).StatusCode);

        var student = await StudentAsync(account);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadRawAsync(student, discipline.Id, Pdf("x"), "x.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.DeleteAsync($"/api/manager/disciplines/{discipline.Id}/program")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).StatusCode);

        Assert.Equal(original, await (await manager.GetAsync($"/api/manager/disciplines/{discipline.Id}/program")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Excel_import_and_field_edits_keep_the_programme()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager, "old");
        await UploadAsync(manager, discipline.Id, Pdf("keep"), "keep.pdf");
        var key = await StorageKeyAsync(discipline.Id);

        var preview = await PreviewImportAsync(manager, discipline.Name, "updated by import");
        Assert.Equal(1, preview.UpdateCount);
        await manager.PostJsonAsync<ImportConfirmResultDto>("/api/manager/imports/disciplines/confirm", new { importId = preview.ImportId });

        var afterImport = await manager.GetJsonAsync<DisciplineDto>($"/api/manager/disciplines/{discipline.Id}");
        Assert.Equal("updated by import", afterImport.Description);
        Assert.Equal("keep.pdf", afterImport.Program?.FileName);

        var edited = await manager.PutJsonAsync<DisciplineDto>($"/api/manager/disciplines/{discipline.Id}",
            new { name = discipline.Name, description = "edited" });
        Assert.Equal("keep.pdf", edited.Program?.FileName);
        Assert.Equal(key, await StorageKeyAsync(discipline.Id));
        Assert.True(File.Exists(StoredFile(key!.Value)));
    }

    [Fact]
    public async Task Deleting_a_discipline_removes_its_programme_file()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager);
        await UploadAsync(manager, discipline.Id, Pdf("bye"), "bye.pdf");
        var key = await StorageKeyAsync(discipline.Id);

        await (await manager.DeleteAsync($"/api/manager/disciplines/{discipline.Id}")).EnsureStatusAsync(HttpStatusCode.NoContent);
        Assert.Null(await StorageKeyAsync(discipline.Id));
        Assert.False(File.Exists(StoredFile(key!.Value)));
    }

    [Fact]
    public async Task Programme_is_still_available_after_an_application_restart()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager);
        var pdf = Pdf("durable");
        await UploadAsync(manager, discipline.Id, pdf, "durable.pdf");

        // A new host instance over the same database and storage directory, as after a container restart.
        await using var restarted = Factory.WithWebHostBuilder(_ => { });
        var client = new ApiClient(restarted.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false
        }));
        await client.LoginAsync(ApiFactory.ManagerEmail, ApiFactory.ManagerPassword);
        var res = await client.GetAsync($"/api/manager/disciplines/{discipline.Id}/program");
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(pdf, await res.Content.ReadAsByteArrayAsync());
    }

    private static byte[] Pdf(string marker, int size = 0)
    {
        var head = Encoding.ASCII.GetBytes($"%PDF-1.7\n% {marker}\n1 0 obj << /Type /Catalog >> endobj\n");
        var tail = Encoding.ASCII.GetBytes("trailer << /Root 1 0 R >>\n%%EOF\n");
        var padding = Math.Max(0, size - head.Length - tail.Length);
        return [.. head, .. Enumerable.Repeat((byte)' ', padding), .. tail];
    }

    private static async Task<HttpResponseMessage> UploadRawAsync(ApiClient client, Guid disciplineId, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", fileName);
        return await client.Http.PostAsync($"/api/manager/disciplines/{disciplineId}/program", content);
    }

    private static async Task<DisciplineProgramDto> UploadAsync(ApiClient client, Guid disciplineId, byte[] bytes, string fileName)
    {
        var res = await UploadRawAsync(client, disciplineId, bytes, fileName);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<DisciplineProgramDto>(ApiClient.Json))!;
    }

    private static async Task AssertRejectedAsync(ApiClient client, Guid disciplineId, byte[] bytes, string fileName,
        HttpStatusCode status, string code)
    {
        var res = await UploadRawAsync(client, disciplineId, bytes, fileName);
        await res.EnsureStatusAsync(status);
        Assert.Equal(code, await res.ErrorCodeAsync());
    }

    private async Task<Guid?> StorageKeyAsync(Guid disciplineId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DisciplinePrograms.Where(p => p.DisciplineId == disciplineId)
            .Select(p => (Guid?)p.StorageKey).FirstOrDefaultAsync();
    }

    private string StoredFile(Guid key) => Path.Combine(Factory.DocumentsPath, key.ToString("N") + ".pdf");

    private int StoredFileCount() =>
        Directory.Exists(Factory.DocumentsPath) ? Directory.GetFiles(Factory.DocumentsPath).Length : 0;

    private static async Task<ImportPreviewDto> PreviewImportAsync(ApiClient manager, string name, string description)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Дисциплины");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Описание";
        ws.Cell(2, 1).Value = name;
        ws.Cell(2, 2).Value = description;
        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(ExcelExporter.ContentType);
        content.Add(file, "file", "disciplines.xlsx");
        var res = await manager.Http.PostAsync("/api/manager/imports/disciplines/preview", content);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(ApiClient.Json))!;
    }
}
