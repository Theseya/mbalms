using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class ImportSessionIsolationTests(ApiFactory factory) : TestBase(factory)
{
    [Fact]
    public async Task Other_manager_cannot_confirm_foreign_import_and_nothing_is_written()
    {
        var owner = await ManagerAsync();
        var (intruder, _, _) = await CreateManagerAsync();
        var groupName = Unique("ImpIso");

        var preview = await PreviewGroupsAsync(owner, groupName);
        Assert.False(string.IsNullOrEmpty(preview.ImportId));
        Assert.Equal(1, preview.CreateCount);
        Assert.DoesNotContain(await owner.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All"),
            g => g.Name == groupName);

        var stolen = await intruder.PostAsync("/api/manager/imports/groups/confirm", new { importId = preview.ImportId });
        await stolen.EnsureStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ErrorCodes.ImportExpired, await stolen.ErrorCodeAsync());

        Assert.DoesNotContain(await owner.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All"),
            g => g.Name == groupName);

        var result = await owner.PostJsonAsync<ImportConfirmResultDto>(
            "/api/manager/imports/groups/confirm", new { importId = preview.ImportId });
        Assert.Equal(1, result.Created);
        Assert.Contains(await owner.GetJsonAsync<List<GroupDto>>("/api/manager/groups?status=All"),
            g => g.Name == groupName);
    }

    private static async Task<ImportPreviewDto> PreviewGroupsAsync(ApiClient manager, string name)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Группы");
        ws.Cell(1, 1).Value = "Название";
        ws.Cell(1, 2).Value = "Дата начала";
        ws.Cell(1, 3).Value = "Дата окончания";
        ws.Cell(2, 1).Value = name;
        ws.Cell(2, 2).Value = "2031-01-01";
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
}
