using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace MbaLms.Api.Infrastructure.Documents;

public static class PdfResult
{
    /// <summary>
    /// Inline PDF so the browser opens its viewer instead of downloading. Not cached by shared caches:
    /// the response depends on who is signed in.
    /// </summary>
    public static IActionResult Inline(ControllerBase controller, Stream content, string fileName)
    {
        var headers = controller.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileName = "program.pdf",
            FileNameStar = fileName,
        }.ToString();
        return controller.File(content, PdfFile.ContentType, enableRangeProcessing: true);
    }
}
