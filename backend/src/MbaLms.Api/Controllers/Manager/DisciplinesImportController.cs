using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

[Route("api/manager/imports/disciplines")]
public class DisciplinesImportController(AppDbContext db, CurrentUser current, ImportSessionStore sessions)
    : ManagerControllerBase(db)
{
    public const string Entity = "disciplines";
    private const int NameMax = 200;
    private const int DescriptionMax = 2000;

    private static readonly ImportColumnSpec[] Columns =
    [
        new("name", ["Название", "Name"], Required: true),
        new("description", ["Описание", "Description"])
    ];

    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? lang = null)
    {
        var t = ExportText.For(lang);
        var bytes = ExcelExporter.Build(t["sheet.disciplines"], new ExcelColumn<object>[]
        {
            new(t["col.name"], _ => null),
            new(t["col.description"], _ => null)
        }, []);
        return File(bytes, ExcelExporter.ContentType, $"disciplines_template_{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
    }

    [HttpPost("preview")]
    [RequestSizeLimit(2_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_000_000)]
    public async Task<ImportPreviewDto> Preview(IFormFile? file, CancellationToken ct)
    {
        await using var stream = await OpenUploadAsync(file);
        var table = ExcelImportReader.Read(stream, Columns);
        if (table.CellErrors.Count > 0)
        {
            return new ImportPreviewDto(
                ImportId: string.Empty,
                CreateCount: 0, UpdateCount: 0, ConflictCount: 0, ErrorCount: table.CellErrors.Count,
                Rows: [],
                FileErrors: table.CellErrors);
        }

        var existing = await Db.Disciplines.AsNoTracking().Select(d => new { d.Id, d.Name, d.Description }).ToListAsync(ct);
        var payload = new DisciplineImportPayload();
        var fileNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in table.Rows)
        {
            var name = raw.Values.GetValueOrDefault("name");
            var description = raw.Values.GetValueOrDefault("description");
            var row = new DisciplineImportRow
            {
                RowNumber = raw.RowNumber,
                Name = name?.Trim() ?? string.Empty,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            if (string.IsNullOrWhiteSpace(row.Name))
                row.Errors.Add(new ImportRowError("name", FieldCodes.Required, table.SheetName, raw.RowNumber, "name"));
            else if (row.Name.Length > NameMax)
                row.Errors.Add(new ImportRowError("name", FieldCodes.MaxLength, table.SheetName, raw.RowNumber, "name"));

            if (row.Description is { Length: > DescriptionMax })
                row.Errors.Add(new ImportRowError("description", FieldCodes.MaxLength, table.SheetName, raw.RowNumber, "description"));

            if (row.Errors.Count == 0)
            {
                var key = row.Name;
                fileNameCounts[key] = fileNameCounts.GetValueOrDefault(key) + 1;
            }

            payload.Rows.Add(row);
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count == 0))
        {
            if (fileNameCounts.GetValueOrDefault(row.Name) > 1)
            {
                row.Action = ImportRowAction.Conflict;
                row.Errors.Add(new ImportRowError("name", FieldCodes.Ambiguous, null, row.RowNumber));
                continue;
            }

            var matches = existing.Where(d => string.Equals(d.Name, row.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
            {
                row.Action = ImportRowAction.Conflict;
                row.Errors.Add(new ImportRowError("name", FieldCodes.Ambiguous, null, row.RowNumber));
            }
            else if (matches.Count == 1)
            {
                row.Action = ImportRowAction.Update;
                row.MatchedId = matches[0].Id;
            }
            else
            {
                row.Action = ImportRowAction.Create;
            }
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count > 0 && r.Action != ImportRowAction.Conflict))
            row.Action = ImportRowAction.Error;

        var importId = sessions.Save(current.UserId, Entity, payload);
        return ToPreview(importId, payload, []);
    }

    [HttpPost("confirm")]
    public async Task<ImportConfirmResultDto> Confirm([FromBody] ImportConfirmRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ImportId))
            throw AppException.Validation(nameof(request.ImportId), FieldCodes.Required);

        var session = sessions.Take(request.ImportId.Trim(), current.UserId, Entity);
        if (session is null)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        if (session.Payload is not DisciplineImportPayload payload)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        var writable = payload.Rows.Where(r => r.Action is ImportRowAction.Create or ImportRowAction.Update).ToList();
        if (writable.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportEmpty);

        // Re-validate against current DB (state may have changed since preview).
        var existing = await Db.Disciplines.ToListAsync(ct);
        var plannedCreates = new List<DisciplineImportRow>();
        var plannedUpdates = new List<(DisciplineImportRow Row, Discipline Entity)>();

        foreach (var row in writable)
        {
            if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > NameMax
                || row.Description is { Length: > DescriptionMax })
                throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);

            var matches = existing.Where(d => string.Equals(d.Name, row.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
                throw AppException.Validation("name", FieldCodes.Ambiguous);

            if (row.Action == ImportRowAction.Create)
            {
                if (matches.Count != 0)
                    throw AppException.Validation("name", ErrorCodes.Duplicate);
                // Also conflict with another create in this batch (already filtered at preview, re-check).
                if (plannedCreates.Any(c => string.Equals(c.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                    throw AppException.Validation("name", FieldCodes.Ambiguous);
                plannedCreates.Add(row);
            }
            else
            {
                if (matches.Count != 1)
                    throw AppException.Validation("name", FieldCodes.NotFound);
                var entity = matches[0];
                if (row.MatchedId is Guid id && id != entity.Id)
                    throw AppException.Validation("name", FieldCodes.Ambiguous);
                plannedUpdates.Add((row, entity));
            }
        }

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in plannedCreates)
            {
                Db.Disciplines.Add(new Discipline
                {
                    Id = Guid.CreateVersion7(),
                    Name = row.Name.Trim(),
                    Description = Clean(row.Description)
                });
            }
            foreach (var (row, entity) in plannedUpdates)
            {
                entity.Name = row.Name.Trim();
                entity.Description = Clean(row.Description);
            }
            await Db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return new ImportConfirmResultDto(plannedCreates.Count, plannedUpdates.Count, payload.Rows.Count - writable.Count);
    }

    private static ImportPreviewDto ToPreview(string importId, DisciplineImportPayload payload, IReadOnlyList<ImportCellError> fileErrors) =>
        new(
            importId,
            payload.Rows.Count(r => r.Action == ImportRowAction.Create),
            payload.Rows.Count(r => r.Action == ImportRowAction.Update),
            payload.Rows.Count(r => r.Action == ImportRowAction.Conflict),
            payload.Rows.Count(r => r.Action == ImportRowAction.Error) + fileErrors.Count,
            payload.Rows.Select(r => new ImportPreviewRowDto(
                r.RowNumber,
                r.Action,
                new Dictionary<string, string?> { ["name"] = r.Name, ["description"] = r.Description },
                r.Errors)).ToList(),
            fileErrors);

    private static async Task<Stream> OpenUploadAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
        var name = file.FileName ?? string.Empty;
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
        var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        ms.Position = 0;
        return ms;
    }
}
