using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Documents;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MbaLms.Api.Controllers.Manager;

public record DisciplineProgramDto(string FileName, long SizeBytes, DateTimeOffset UploadedAt);

public record DisciplineProgramLimitsDto(long MaxBytes);

/// <summary>
/// One programme PDF (ПУД) per discipline. Upload and replacement keep the previous document until the new one
/// is stored and recorded; only then is the old file removed.
/// </summary>
[Route("api/manager/disciplines")]
public class DisciplineProgramsController(
    AppDbContext db, DocumentStorage storage, IOptions<DocumentOptions> options, CurrentUser current, AppTime time)
    : ManagerControllerBase(db)
{
    /// <summary>Room for multipart boundaries and headers on top of the file itself.</summary>
    private const long FormOverhead = 64 * 1024;

    private long MaxBytes => options.Value.MaxProgramPdfBytes;

    [HttpGet("program-limits")]
    public DisciplineProgramLimitsDto Limits() => new(MaxBytes);

    [HttpGet("{id:guid}/program")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var program = await Db.DisciplinePrograms.AsNoTracking().FirstOrDefaultAsync(p => p.DisciplineId == id, ct)
                      ?? throw AppException.NotFound();
        var stream = storage.OpenRead(program.StorageKey) ?? throw AppException.NotFound();
        return PdfResult.Inline(this, stream, program.FileName);
    }

    [HttpPost("{id:guid}/program")]
    [DisableFormValueModelBinding]
    public async Task<DisciplineProgramDto> Upload(Guid id, CancellationToken ct)
    {
        var limit = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBytes + FormOverhead;
        if (Request.ContentLength > MaxBytes + FormOverhead) throw TooLarge();

        if (!await Db.Disciplines.AnyAsync(d => d.Id == id, ct)) throw AppException.NotFound();
        if (!Request.HasFormContentType) throw AppException.BadRequest(ErrorCodes.FileRequired);

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxBytes + FormOverhead }, ct);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw TooLarge();
        }
        catch (InvalidDataException e) when (e.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            throw TooLarge();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or BadHttpRequestException)
        {
            // Malformed or empty multipart body.
            throw AppException.BadRequest(ErrorCodes.FileRequired);
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) throw AppException.BadRequest(ErrorCodes.FileRequired);
        if (file.Length > MaxBytes) throw TooLarge();
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw AppException.BadRequest(ErrorCodes.FileNotPdf);

        await using var content = await SeekableAsync(file, ct);
        if (!await PdfFile.LooksLikePdfAsync(content, ct)) throw AppException.BadRequest(ErrorCodes.FileNotPdf);

        var newKey = await storage.SaveAsync(content, ct);
        var program = await Db.DisciplinePrograms.FirstOrDefaultAsync(p => p.DisciplineId == id, ct);
        var oldKey = program?.StorageKey;
        if (program is null)
        {
            program = new DisciplineProgram { DisciplineId = id, FileName = string.Empty };
            Db.DisciplinePrograms.Add(program);
        }
        program.StorageKey = newKey;
        program.FileName = PdfFile.DisplayName(file.FileName);
        program.SizeBytes = file.Length;
        program.UploadedAt = time.UtcNow;
        program.UploadedByUserId = current.UserId;

        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch
        {
            // The previous document stays recorded and on disk; only the unused new file is removed.
            storage.Delete(newKey);
            throw;
        }

        if (oldKey is Guid old) storage.Delete(old);
        return ToDto(program);
    }

    [HttpDelete("{id:guid}/program")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var program = await Db.DisciplinePrograms.FirstOrDefaultAsync(p => p.DisciplineId == id, ct)
                      ?? throw AppException.NotFound();
        var key = program.StorageKey;
        Db.DisciplinePrograms.Remove(program);
        await Db.SaveChangesAsync(ct);
        storage.Delete(key);
        return NoContent();
    }

    public static DisciplineProgramDto ToDto(DisciplineProgram p) => new(p.FileName, p.SizeBytes, p.UploadedAt);

    private AppException TooLarge() => new(StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge);

    private static async Task<Stream> SeekableAsync(IFormFile file, CancellationToken ct)
    {
        var stream = file.OpenReadStream();
        if (stream.CanSeek) return stream;
        var copy = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (stream) await stream.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }
}
