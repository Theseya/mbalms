using System.Net;
using ClosedXML.Excel;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class ExportTests(ApiFactory factory) : TestBase(factory)
{
    [Theory]
    [InlineData("students")]
    [InlineData("groups")]
    [InlineData("teachers")]
    [InlineData("disciplines")]
    [InlineData("grades")]
    [InlineData("schedule")]
    public async Task Every_export_returns_a_valid_xlsx(string kind)
    {
        var manager = await ManagerAsync();
        var res = await manager.GetAsync($"/api/manager/exports/{kind}");
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(ExcelExporter.ContentType, res.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName);

        using var wb = new XLWorkbook(await res.Content.ReadAsStreamAsync());
        Assert.False(wb.Worksheet(1).Cell(1, 1).IsEmpty());
    }

    [Fact]
    public async Task Students_export_has_headers_and_cyrillic_data()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        await CreateStudentAsync(manager, group.Id, "Щукина");

        using var wb = await DownloadAsync(manager, $"/api/manager/exports/students?groupId={group.Id}");
        var ws = wb.Worksheet(1);
        Assert.Equal("Фамилия", ws.Cell(1, 1).GetString());
        Assert.Equal("Email", ws.Cell(1, 4).GetString());
        Assert.Equal("Щукина", ws.Cell(2, 1).GetString());
        Assert.Equal(group.Name, ws.Cell(2, 5).GetString());

        using var en = await DownloadAsync(manager, $"/api/manager/exports/students?groupId={group.Id}&lang=en");
        Assert.Equal("Last name", en.Worksheet(1).Cell(1, 1).GetString());
    }

    [Fact]
    public async Task Formula_like_text_is_exported_as_literal_text()
    {
        var manager = await ManagerAsync();
        var discipline = await CreateDisciplineAsync(manager, "=HYPERLINK(\"http://evil\",\"x\")");

        using var wb = await DownloadAsync(manager, "/api/manager/exports/disciplines");
        var row = wb.Worksheet(1).RowsUsed().First(r => r.Cell(1).GetString() == discipline.Name);
        var cell = row.Cell(2);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.True(cell.Style.IncludeQuotePrefix);
        Assert.Equal("=HYPERLINK(\"http://evil\",\"x\")", cell.GetString());
    }

    [Fact]
    public async Task Schedule_export_uses_app_local_time()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        await CreateLessonAsync(manager, group.Id, "2030-04-01T09:30", "2030-04-01T11:00");

        using var wb = await DownloadAsync(manager, $"/api/manager/exports/schedule?groupId={group.Id}");
        var ws = wb.Worksheet(1);
        Assert.Equal(new DateTime(2030, 4, 1), ws.Cell(2, 1).GetDateTime());
        Assert.Equal(new TimeSpan(9, 30, 0), ws.Cell(2, 2).Value.GetTimeSpan());
        Assert.Equal("09:30", ws.Cell(2, 2).GetFormattedString());
        Assert.Equal("11:00", ws.Cell(2, 3).GetFormattedString());
    }

    [Fact]
    public async Task Students_cannot_export()
    {
        var manager = await ManagerAsync();
        var group = await CreateGroupAsync(manager);
        var student = await StudentAsync(await CreateStudentAsync(manager, group.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/manager/exports/students")).StatusCode);
    }

    private static async Task<XLWorkbook> DownloadAsync(ApiClient client, string url)
    {
        var res = await client.GetAsync(url);
        await res.EnsureStatusAsync(HttpStatusCode.OK);
        return new XLWorkbook(await res.Content.ReadAsStreamAsync());
    }
}

public class SpreadsheetSanitizerTests
{
    [Theory]
    [InlineData("=SUM(A1:A2)", true)]
    [InlineData("+7 999", true)]
    [InlineData("-5", true)]
    [InlineData("@cmd", true)]
    [InlineData("Иванов", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_formula_like_text(string? value, bool expected) =>
        Assert.Equal(expected, SpreadsheetSanitizer.IsFormulaLike(value));

    [Fact]
    public void Csv_escaping_prefixes_apostrophe() =>
        Assert.Equal("'=1+1", SpreadsheetSanitizer.EscapeForCsv("=1+1"));
}
