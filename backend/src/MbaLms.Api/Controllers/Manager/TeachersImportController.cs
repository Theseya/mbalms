using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

[Route("api/manager/imports/teachers")]
public class TeachersImportController(AppDbContext db, CurrentUser current, ImportSessionStore sessions)
    : ManagerControllerBase(db)
{
    public const string Entity = "teachers";
    private const int NameMax = 100;
    private const int EmailMax = 256;

    private static readonly ImportColumnSpec[] Columns =
    [
        new("lastName", ["Фамилия", "Last name"], Required: true),
        new("firstName", ["Имя", "First name"], Required: true),
        new("middleName", ["Отчество", "Middle name"]),
        new("email", ["Email"])
    ];

    private static readonly EmailAddressAttribute EmailValidator = new();

    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? lang = null)
    {
        var t = ExportText.For(lang);
        var bytes = ExcelExporter.Build(t["sheet.teachers"], new ExcelColumn<object>[]
        {
            new(t["col.lastName"], _ => null),
            new(t["col.firstName"], _ => null),
            new(t["col.middleName"], _ => null),
            new(t["col.email"], _ => null)
        }, []);
        return File(bytes, ExcelExporter.ContentType, $"teachers_template_{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
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

        var existing = await Db.Teachers.AsNoTracking()
            .Select(x => new TeacherSnap(x.Id, x.LastName, x.FirstName, x.MiddleName, x.Email))
            .ToListAsync(ct);
        var payload = new TeacherImportPayload();
        var fileEmailCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileFioCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in table.Rows)
        {
            var row = ParseRow(raw, table.SheetName);
            payload.Rows.Add(row);

            if (row.Errors.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(row.Email))
                    fileEmailCounts[row.Email] = fileEmailCounts.GetValueOrDefault(row.Email) + 1;
                var fioKey = FioKey(row.LastName, row.FirstName, row.MiddleName);
                fileFioCounts[fioKey] = fileFioCounts.GetValueOrDefault(fioKey) + 1;
            }
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count == 0))
        {
            if (!string.IsNullOrWhiteSpace(row.Email) && fileEmailCounts.GetValueOrDefault(row.Email) > 1)
            {
                MarkConflict(row, "email", FieldCodes.Ambiguous);
                continue;
            }

            if (fileFioCounts.GetValueOrDefault(FioKey(row.LastName, row.FirstName, row.MiddleName)) > 1)
            {
                MarkConflict(row, "lastName", FieldCodes.Ambiguous);
                continue;
            }

            ClassifyRow(row, existing);
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

        if (session.Payload is not TeacherImportPayload payload)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        var writable = payload.Rows.Where(r => r.Action is ImportRowAction.Create or ImportRowAction.Update).ToList();
        if (writable.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportEmpty);

        var existing = await Db.Teachers.ToListAsync(ct);
        var snaps = existing.Select(x => new TeacherSnap(x.Id, x.LastName, x.FirstName, x.MiddleName, x.Email)).ToList();
        var plannedCreates = new List<TeacherImportRow>();
        var plannedUpdates = new List<(TeacherImportRow Row, Teacher Entity)>();

        foreach (var row in writable)
        {
            ValidateRowFields(row);

            var action = ClassifyForConfirm(row, snaps);
            if (action != row.Action)
                throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);

            if (row.Action == ImportRowAction.Create)
            {
                if (!string.IsNullOrWhiteSpace(row.Email)
                    && snaps.Any(t => EmailsEqual(t.Email, row.Email)))
                    throw AppException.Validation("email", ErrorCodes.Duplicate);
                if (plannedCreates.Any(c => FioKey(c.LastName, c.FirstName, c.MiddleName)
                        == FioKey(row.LastName, row.FirstName, row.MiddleName)))
                    throw AppException.Validation("lastName", FieldCodes.Ambiguous);
                plannedCreates.Add(row);
            }
            else
            {
                var entity = existing.SingleOrDefault(t => t.Id == row.MatchedId)
                    ?? throw AppException.Validation("lastName", FieldCodes.NotFound);
                var match = ResolveMatch(row, snaps);
                if (match?.Kind != MatchKind.Ok || match.Teacher!.Id != entity.Id)
                    throw AppException.Validation("lastName", FieldCodes.Ambiguous);
                plannedUpdates.Add((row, entity));
            }
        }

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var row in plannedCreates)
            {
                Db.Teachers.Add(new Teacher
                {
                    Id = Guid.CreateVersion7(),
                    LastName = row.LastName,
                    FirstName = row.FirstName,
                    MiddleName = Clean(row.MiddleName),
                    Email = Clean(row.Email)
                });
            }

            foreach (var (row, entity) in plannedUpdates)
                ApplyUpdate(entity, row, matchedByEmail: !string.IsNullOrWhiteSpace(row.Email)
                    && snaps.Any(t => t.Id == entity.Id && EmailsEqual(t.Email, row.Email)));

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

    private static TeacherImportRow ParseRow(ExcelImportDataRow raw, string sheet)
    {
        var lastName = raw.Values.GetValueOrDefault("lastName");
        var firstName = raw.Values.GetValueOrDefault("firstName");
        var middleName = raw.Values.GetValueOrDefault("middleName");
        var email = raw.Values.GetValueOrDefault("email");

        var row = new TeacherImportRow
        {
            RowNumber = raw.RowNumber,
            LastName = lastName?.Trim() ?? string.Empty,
            FirstName = firstName?.Trim() ?? string.Empty,
            MiddleName = string.IsNullOrWhiteSpace(middleName) ? null : middleName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim()
        };

        if (string.IsNullOrWhiteSpace(row.LastName))
            row.Errors.Add(new ImportRowError("lastName", FieldCodes.Required, sheet, raw.RowNumber, "lastName"));
        else if (row.LastName.Length > NameMax)
            row.Errors.Add(new ImportRowError("lastName", FieldCodes.MaxLength, sheet, raw.RowNumber, "lastName"));

        if (string.IsNullOrWhiteSpace(row.FirstName))
            row.Errors.Add(new ImportRowError("firstName", FieldCodes.Required, sheet, raw.RowNumber, "firstName"));
        else if (row.FirstName.Length > NameMax)
            row.Errors.Add(new ImportRowError("firstName", FieldCodes.MaxLength, sheet, raw.RowNumber, "firstName"));

        if (row.MiddleName is { Length: > NameMax })
            row.Errors.Add(new ImportRowError("middleName", FieldCodes.MaxLength, sheet, raw.RowNumber, "middleName"));

        if (row.Email is { Length: > EmailMax })
            row.Errors.Add(new ImportRowError("email", FieldCodes.MaxLength, sheet, raw.RowNumber, "email"));
        else if (row.Email is not null && !EmailValidator.IsValid(row.Email))
            row.Errors.Add(new ImportRowError("email", FieldCodes.Email, sheet, raw.RowNumber, "email"));

        return row;
    }

    private static void ValidateRowFields(TeacherImportRow row)
    {
        if (string.IsNullOrWhiteSpace(row.LastName) || row.LastName.Length > NameMax
            || string.IsNullOrWhiteSpace(row.FirstName) || row.FirstName.Length > NameMax
            || row.MiddleName is { Length: > NameMax }
            || row.Email is { Length: > EmailMax }
            || row.Email is not null && !EmailValidator.IsValid(row.Email))
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
    }

    private static void ClassifyRow(TeacherImportRow row, IReadOnlyList<TeacherSnap> existing)
    {
        var match = ResolveMatch(row, existing);
        if (match is null)
        {
            row.Action = ImportRowAction.Create;
            return;
        }

        if (match.Kind == MatchKind.Conflict)
        {
            MarkConflict(row, match.ConflictField!, FieldCodes.Ambiguous);
            return;
        }

        row.Action = ImportRowAction.Update;
        row.MatchedId = match.Teacher!.Id;
    }

    private static ImportRowAction ClassifyForConfirm(TeacherImportRow row, IReadOnlyList<TeacherSnap> existing)
    {
        var match = ResolveMatch(row, existing);
        if (match?.Kind == MatchKind.Conflict)
            throw AppException.Validation(match.ConflictField!, FieldCodes.Ambiguous);
        if (match is null)
            return ImportRowAction.Create;
        if (row.MatchedId is Guid id && id != match.Teacher!.Id)
            throw AppException.Validation("lastName", FieldCodes.Ambiguous);
        return ImportRowAction.Update;
    }

    private static MatchResult? ResolveMatch(TeacherImportRow row, IReadOnlyList<TeacherSnap> existing)
    {
        if (!string.IsNullOrWhiteSpace(row.Email))
        {
            var byEmail = existing.Where(t => EmailsEqual(t.Email, row.Email)).ToList();
            if (byEmail.Count > 1)
                return MatchResult.Conflict("email");
            if (byEmail.Count == 1)
                return MatchResult.Ok(byEmail[0]);
        }

        var byFio = existing.Where(t => SameFio(t, row.LastName, row.FirstName, row.MiddleName)).ToList();
        if (byFio.Count > 1)
            return MatchResult.Conflict("lastName");
        if (byFio.Count == 0)
            return null;

        var teacher = byFio[0];
        if (!string.IsNullOrWhiteSpace(row.Email)
            && !string.IsNullOrWhiteSpace(teacher.Email)
            && !EmailsEqual(teacher.Email, row.Email))
            return MatchResult.Conflict("email");

        return MatchResult.Ok(teacher);
    }

    private static void ApplyUpdate(Teacher entity, TeacherImportRow row, bool matchedByEmail)
    {
        entity.LastName = row.LastName;
        entity.FirstName = row.FirstName;
        entity.MiddleName = Clean(row.MiddleName);

        if (matchedByEmail)
            entity.Email = Clean(row.Email);
        else if (string.IsNullOrWhiteSpace(entity.Email))
            entity.Email = Clean(row.Email);
    }

    private static void MarkConflict(TeacherImportRow row, string field, string code)
    {
        row.Action = ImportRowAction.Conflict;
        row.Errors.Add(new ImportRowError(field, code, null, row.RowNumber));
    }

    private static string FioKey(string last, string first, string? middle) =>
        $"{last.Trim().ToUpperInvariant()}|{first.Trim().ToUpperInvariant()}|{(string.IsNullOrWhiteSpace(middle) ? "" : middle.Trim().ToUpperInvariant())}";

    private static bool SameFio(TeacherSnap t, string last, string first, string? middle) =>
        string.Equals(t.LastName, last, StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.FirstName, first, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Clean(t.MiddleName) ?? string.Empty, Clean(middle) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool EmailsEqual(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static ImportPreviewDto ToPreview(string importId, TeacherImportPayload payload, IReadOnlyList<ImportCellError> fileErrors) =>
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
                    ["lastName"] = r.LastName,
                    ["firstName"] = r.FirstName,
                    ["middleName"] = r.MiddleName,
                    ["email"] = r.Email
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

    private sealed record TeacherSnap(Guid Id, string LastName, string FirstName, string? MiddleName, string? Email);

    private enum MatchKind { Ok, Conflict }

    private sealed record MatchResult(MatchKind Kind, TeacherSnap? Teacher, string? ConflictField)
    {
        public static MatchResult Ok(TeacherSnap t) => new(MatchKind.Ok, t, null);
        public static MatchResult Conflict(string field) => new(MatchKind.Conflict, null, field);
    }
}
