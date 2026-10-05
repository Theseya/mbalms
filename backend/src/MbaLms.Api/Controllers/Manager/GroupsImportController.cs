using System.Globalization;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

[Route("api/manager/imports/groups")]
public class GroupsImportController(AppDbContext db, CurrentUser current, ImportSessionStore sessions)
    : ManagerControllerBase(db)
{
    public const string Entity = "groups";
    private const int NameMax = 100;

    private static readonly ImportColumnSpec[] Columns =
    [
        new("name", ["Название", "Name"], Required: true),
        new("startDate", ["Дата начала", "Start date"]),
        new("endDate", ["Дата окончания", "End date"])
    ];

    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? lang = null)
    {
        var t = ExportText.For(lang);
        var bytes = ExcelExporter.Build(t["sheet.groups"], new ExcelColumn<object>[]
        {
            new(t["col.name"], _ => null),
            new(t["col.startDate"], _ => null),
            new(t["col.endDate"], _ => null)
        }, []);
        return File(bytes, ExcelExporter.ContentType, $"groups_template_{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
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

        var existing = await Db.Groups.AsNoTracking()
            .Select(g => new GroupSnap(g.Id, g.Name))
            .ToListAsync(ct);
        var payload = new GroupImportPayload();
        var fileNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in table.Rows)
        {
            var row = ParseRow(raw, table.SheetName);
            payload.Rows.Add(row);
            if (row.Errors.Count == 0)
                fileNameCounts[row.Name] = fileNameCounts.GetValueOrDefault(row.Name) + 1;
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count == 0))
        {
            if (fileNameCounts.GetValueOrDefault(row.Name) > 1)
            {
                MarkConflict(row, "name", FieldCodes.Ambiguous);
                continue;
            }

            var matches = existing.Where(g => string.Equals(g.Name, row.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
            {
                MarkConflict(row, "name", FieldCodes.Ambiguous);
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

        if (session.Payload is not GroupImportPayload payload)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        var writable = payload.Rows.Where(r => r.Action is ImportRowAction.Create or ImportRowAction.Update).ToList();
        if (writable.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportEmpty);

        var existing = await Db.Groups.ToListAsync(ct);
        var plannedCreates = new List<GroupImportRow>();
        var plannedUpdates = new List<(GroupImportRow Row, Group Entity)>();

        foreach (var row in writable)
        {
            if (string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > NameMax)
                throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
            if (row.StartDate is not null && row.EndDate is not null && row.EndDate < row.StartDate)
                throw AppException.Validation("endDate", FieldCodes.EndBeforeStart);

            var matches = existing.Where(g => string.Equals(g.Name, row.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
                throw AppException.Validation("name", FieldCodes.Ambiguous);

            if (row.Action == ImportRowAction.Create)
            {
                if (matches.Count != 0)
                    throw AppException.Validation("name", ErrorCodes.Duplicate);
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

        var programId = await Db.Programs.Select(p => p.Id).FirstAsync(ct);

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in plannedCreates)
            {
                Db.Groups.Add(new Group
                {
                    Id = Guid.CreateVersion7(),
                    ProgramId = programId,
                    Name = row.Name.Trim(),
                    StartDate = row.StartDate,
                    EndDate = row.EndDate,
                    Status = GroupStatus.Active
                });
            }

            foreach (var (row, entity) in plannedUpdates)
            {
                // Name and dates only — Status / ArchivedAt stay as-is (including archived groups).
                entity.Name = row.Name.Trim();
                entity.StartDate = row.StartDate;
                entity.EndDate = row.EndDate;
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

    private static GroupImportRow ParseRow(ExcelImportDataRow raw, string sheet)
    {
        var name = raw.Values.GetValueOrDefault("name")?.Trim() ?? string.Empty;
        var startRaw = raw.Values.GetValueOrDefault("startDate");
        var endRaw = raw.Values.GetValueOrDefault("endDate");
        var errors = new List<ImportRowError>();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add(new ImportRowError("name", FieldCodes.Required, sheet, raw.RowNumber, "name"));
        else if (name.Length > NameMax)
            errors.Add(new ImportRowError("name", FieldCodes.MaxLength, sheet, raw.RowNumber, "name"));

        DateOnly? startDate = null;
        DateOnly? endDate = null;

        if (!string.IsNullOrWhiteSpace(startRaw))
        {
            if (!TryParseDate(startRaw, out var d))
                errors.Add(new ImportRowError("startDate", FieldCodes.Invalid, sheet, raw.RowNumber, "startDate"));
            else
                startDate = d;
        }

        if (!string.IsNullOrWhiteSpace(endRaw))
        {
            if (!TryParseDate(endRaw, out var d))
                errors.Add(new ImportRowError("endDate", FieldCodes.Invalid, sheet, raw.RowNumber, "endDate"));
            else
                endDate = d;
        }

        if (startDate is not null && endDate is not null && endDate < startDate)
            errors.Add(new ImportRowError("endDate", FieldCodes.EndBeforeStart, sheet, raw.RowNumber, "endDate"));

        return new GroupImportRow
        {
            RowNumber = raw.RowNumber,
            Name = name,
            StartDate = startDate,
            EndDate = endDate,
            Errors = errors
        };
    }

    private static bool TryParseDate(string value, out DateOnly date)
    {
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (DateOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        date = default;
        return false;
    }

    private static void MarkConflict(GroupImportRow row, string field, string code)
    {
        row.Action = ImportRowAction.Conflict;
        row.Errors.Add(new ImportRowError(field, code, null, row.RowNumber));
    }

    private static ImportPreviewDto ToPreview(string importId, GroupImportPayload payload, IReadOnlyList<ImportCellError> fileErrors) =>
        new(
            importId,
            payload.Rows.Count(r => r.Action == ImportRowAction.Create),
            payload.Rows.Count(r => r.Action == ImportRowAction.Update),
            payload.Rows.Count(r => r.Action == ImportRowAction.Conflict),
            payload.Rows.Count(r => r.Action == ImportRowAction.Error) + fileErrors.Count,
            payload.Rows.Select(r => new ImportPreviewRowDto(
                r.RowNumber,
                r.Action,
                new Dictionary<string, string?>
                {
                    ["name"] = r.Name,
                    ["startDate"] = r.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["endDate"] = r.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                },
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

    private sealed record GroupSnap(Guid Id, string Name);
}
