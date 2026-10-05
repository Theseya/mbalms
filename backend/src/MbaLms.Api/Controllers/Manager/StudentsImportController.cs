using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Import;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

[Route("api/manager/imports/students")]
public class StudentsImportController(
    AppDbContext db,
    CurrentUser current,
    ImportSessionStore sessions,
    UserManager<AppUser> users)
    : ManagerControllerBase(db)
{
    public const string Entity = "students";
    private const int NameMax = 100;
    private const int EmailMax = 256;
    private const int GroupNameMax = 100;

    private static readonly ImportColumnSpec[] Columns =
    [
        new("lastName", ["Фамилия", "Last name"], Required: true),
        new("firstName", ["Имя", "First name"], Required: true),
        new("middleName", ["Отчество", "Middle name"]),
        new("email", ["Email"], Required: true),
        new("group", ["Группа", "Group"], Required: true)
    ];

    private static readonly EmailAddressAttribute EmailValidator = new();

    [HttpGet("template")]
    public IActionResult Template([FromQuery] string? lang = null)
    {
        var t = ExportText.For(lang);
        var bytes = ExcelExporter.Build(t["sheet.students"], new ExcelColumn<object>[]
        {
            new(t["col.lastName"], _ => null),
            new(t["col.firstName"], _ => null),
            new(t["col.middleName"], _ => null),
            new(t["col.email"], _ => null),
            new(t["col.group"], _ => null)
        }, []);
        return File(bytes, ExcelExporter.ContentType, $"students_template_{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
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

        var groups = await Db.Groups.AsNoTracking()
            .Select(g => new GroupSnap(g.Id, g.Name, g.Status))
            .ToListAsync(ct);
        var students = await Db.Students.AsNoTracking()
            .Select(s => new StudentSnap(s.Id, s.Email, s.UserId))
            .ToListAsync(ct);

        var payload = new StudentImportPayload();
        var fileEmailCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in table.Rows)
        {
            var row = ParseRow(raw, table.SheetName);
            payload.Rows.Add(row);
            if (row.Errors.Count == 0)
                fileEmailCounts[row.Email] = fileEmailCounts.GetValueOrDefault(row.Email) + 1;
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count == 0))
        {
            if (fileEmailCounts.GetValueOrDefault(row.Email) > 1)
            {
                MarkConflict(row, "email", FieldCodes.Ambiguous);
                continue;
            }

            if (!TryResolveGroup(row, groups))
                continue;

            await ClassifyByEmailAsync(row, students);
        }

        foreach (var row in payload.Rows.Where(r => r.Errors.Count > 0 && r.Action != ImportRowAction.Conflict))
            row.Action = ImportRowAction.Error;

        var importId = sessions.Save(current.UserId, Entity, payload);
        return ToPreview(importId, payload, []);
    }

    [HttpPost("confirm")]
    public async Task<ImportConfirmResultDto> Confirm([FromBody] StudentImportConfirmRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ImportId))
            throw AppException.Validation(nameof(request.ImportId), FieldCodes.Required);

        var session = sessions.Take(request.ImportId.Trim(), current.UserId, Entity);
        if (session is null)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        if (session.Payload is not StudentImportPayload payload)
            throw AppException.BadRequest(ErrorCodes.ImportExpired);

        var writable = payload.Rows.Where(r => r.Action is ImportRowAction.Create or ImportRowAction.Update).ToList();
        if (writable.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportEmpty);

        var passwordByRow = (request.Passwords ?? [])
            .GroupBy(p => p.RowNumber)
            .ToDictionary(g => g.Key, g => g.Last().Password);

        // Reject passwords for update rows; require valid passwords for every create — before any DB write.
        foreach (var row in writable.Where(r => r.Action == ImportRowAction.Update))
        {
            if (passwordByRow.TryGetValue(row.RowNumber, out var pwd) && !string.IsNullOrWhiteSpace(pwd))
                throw AppException.Validation($"passwords[{row.RowNumber}]", FieldCodes.Invalid);
        }

        var creates = writable.Where(r => r.Action == ImportRowAction.Create).ToList();
        var validatedPasswords = new Dictionary<int, string>();
        foreach (var row in creates)
        {
            if (!passwordByRow.TryGetValue(row.RowNumber, out var pwd) || string.IsNullOrWhiteSpace(pwd))
                throw AppException.Validation($"passwords[{row.RowNumber}]", FieldCodes.Required);
            await EnsurePasswordValidAsync(pwd, row.RowNumber);
            validatedPasswords[row.RowNumber] = pwd;
        }

        var groups = await Db.Groups.ToListAsync(ct);
        var students = await Db.Students.ToListAsync(ct);
        var plannedCreates = new List<(StudentImportRow Row, string Password, Guid GroupId)>();
        var plannedUpdates = new List<(StudentImportRow Row, Student Entity, Guid GroupId)>();

        foreach (var row in writable)
        {
            ValidateRowFields(row);
            var groupId = ResolveActiveGroupId(row.GroupName, groups)
                ?? throw AppException.Validation("group", FieldCodes.NotFound);

            if (row.Action == ImportRowAction.Create)
            {
                await EnsureCanCreateAsync(row.Email);
                if (plannedCreates.Any(c => string.Equals(c.Row.Email, row.Email, StringComparison.OrdinalIgnoreCase)))
                    throw AppException.Validation("email", FieldCodes.Ambiguous);
                plannedCreates.Add((row, validatedPasswords[row.RowNumber], groupId));
            }
            else
            {
                var entity = students.SingleOrDefault(s => s.Id == row.MatchedStudentId)
                    ?? students.SingleOrDefault(s => string.Equals(s.Email, row.Email, StringComparison.OrdinalIgnoreCase))
                    ?? throw AppException.Validation("email", FieldCodes.NotFound);
                if (!string.Equals(entity.Email, row.Email, StringComparison.OrdinalIgnoreCase))
                    throw AppException.Validation("email", FieldCodes.Ambiguous);
                plannedUpdates.Add((row, entity, groupId));
            }
        }

        await using var tx = await Db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var (row, password, groupId) in plannedCreates)
            {
                var email = row.Email;
                var user = new AppUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true };
                var result = await users.CreateAsync(user, password);
                if (!result.Succeeded) throw IdentityFailure(result);
                result = await users.AddToRoleAsync(user, Roles.Student);
                if (!result.Succeeded) throw IdentityFailure(result);

                Db.Students.Add(new Student
                {
                    Id = Guid.CreateVersion7(),
                    UserId = user.Id,
                    LastName = row.LastName,
                    FirstName = row.FirstName,
                    MiddleName = Clean(row.MiddleName),
                    Email = email,
                    GroupId = groupId
                });
            }

            foreach (var (row, entity, groupId) in plannedUpdates)
            {
                entity.LastName = row.LastName;
                entity.FirstName = row.FirstName;
                entity.MiddleName = Clean(row.MiddleName);
                entity.GroupId = groupId;
                // Email and Identity credentials stay unchanged.
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

    private async Task ClassifyByEmailAsync(StudentImportRow row, List<StudentSnap> students)
    {
        var matches = students.Where(s => string.Equals(s.Email, row.Email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
        {
            MarkConflict(row, "email", FieldCodes.Ambiguous);
            return;
        }

        if (matches.Count == 1)
        {
            row.Action = ImportRowAction.Update;
            row.MatchedStudentId = matches[0].Id;
            return;
        }

        var user = await users.FindByEmailAsync(row.Email);
        if (user is not null)
        {
            // Existing account that is not a Student profile — never convert Manager / other roles.
            row.Action = ImportRowAction.Conflict;
            row.Errors.Add(new ImportRowError("email", FieldCodes.Invalid, null, row.RowNumber));
            return;
        }

        row.Action = ImportRowAction.Create;
    }

    private async Task EnsureCanCreateAsync(string email)
    {
        if (await Db.Students.AnyAsync(s => s.Email.ToLower() == email.ToLower()))
            throw AppException.Validation("email", ErrorCodes.Duplicate);
        var user = await users.FindByEmailAsync(email);
        if (user is not null)
            throw AppException.Validation("email", FieldCodes.Invalid);
    }

    private async Task EnsurePasswordValidAsync(string password, int rowNumber)
    {
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, new AppUser(), password);
            if (!result.Succeeded)
                throw AppException.Validation($"passwords[{rowNumber}]", FieldCodes.PasswordWeak);
        }
    }

    private static bool TryResolveGroup(StudentImportRow row, IReadOnlyList<GroupSnap> groups)
    {
        var matches = groups.Where(g => string.Equals(g.Name, row.GroupName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
        {
            MarkConflict(row, "group", FieldCodes.Ambiguous);
            return false;
        }

        if (matches.Count == 0)
        {
            row.Errors.Add(new ImportRowError("group", FieldCodes.NotFound, null, row.RowNumber));
            return false;
        }

        if (matches[0].Status != GroupStatus.Active)
        {
            row.Errors.Add(new ImportRowError("group", ErrorCodes.GroupArchived, null, row.RowNumber));
            return false;
        }

        row.ResolvedGroupId = matches[0].Id;
        return true;
    }

    private static Guid? ResolveActiveGroupId(string groupName, IReadOnlyList<Group> groups)
    {
        var matches = groups.Where(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) return null;
        if (matches[0].Status != GroupStatus.Active) return null;
        return matches[0].Id;
    }

    private static StudentImportRow ParseRow(ExcelImportDataRow raw, string sheet)
    {
        var lastName = raw.Values.GetValueOrDefault("lastName")?.Trim() ?? string.Empty;
        var firstName = raw.Values.GetValueOrDefault("firstName")?.Trim() ?? string.Empty;
        var middleName = raw.Values.GetValueOrDefault("middleName");
        var email = raw.Values.GetValueOrDefault("email")?.Trim() ?? string.Empty;
        var group = raw.Values.GetValueOrDefault("group")?.Trim() ?? string.Empty;
        var errors = new List<ImportRowError>();

        if (string.IsNullOrWhiteSpace(lastName))
            errors.Add(new ImportRowError("lastName", FieldCodes.Required, sheet, raw.RowNumber, "lastName"));
        else if (lastName.Length > NameMax)
            errors.Add(new ImportRowError("lastName", FieldCodes.MaxLength, sheet, raw.RowNumber, "lastName"));

        if (string.IsNullOrWhiteSpace(firstName))
            errors.Add(new ImportRowError("firstName", FieldCodes.Required, sheet, raw.RowNumber, "firstName"));
        else if (firstName.Length > NameMax)
            errors.Add(new ImportRowError("firstName", FieldCodes.MaxLength, sheet, raw.RowNumber, "firstName"));

        var middle = string.IsNullOrWhiteSpace(middleName) ? null : middleName.Trim();
        if (middle is { Length: > NameMax })
            errors.Add(new ImportRowError("middleName", FieldCodes.MaxLength, sheet, raw.RowNumber, "middleName"));

        if (string.IsNullOrWhiteSpace(email))
            errors.Add(new ImportRowError("email", FieldCodes.Required, sheet, raw.RowNumber, "email"));
        else if (email.Length > EmailMax)
            errors.Add(new ImportRowError("email", FieldCodes.MaxLength, sheet, raw.RowNumber, "email"));
        else if (!EmailValidator.IsValid(email))
            errors.Add(new ImportRowError("email", FieldCodes.Email, sheet, raw.RowNumber, "email"));

        if (string.IsNullOrWhiteSpace(group))
            errors.Add(new ImportRowError("group", FieldCodes.Required, sheet, raw.RowNumber, "group"));
        else if (group.Length > GroupNameMax)
            errors.Add(new ImportRowError("group", FieldCodes.MaxLength, sheet, raw.RowNumber, "group"));

        return new StudentImportRow
        {
            RowNumber = raw.RowNumber,
            LastName = lastName,
            FirstName = firstName,
            MiddleName = middle,
            Email = email,
            GroupName = group,
            Errors = errors
        };
    }

    private static void ValidateRowFields(StudentImportRow row)
    {
        if (string.IsNullOrWhiteSpace(row.LastName) || row.LastName.Length > NameMax
            || string.IsNullOrWhiteSpace(row.FirstName) || row.FirstName.Length > NameMax
            || row.MiddleName is { Length: > NameMax }
            || string.IsNullOrWhiteSpace(row.Email) || row.Email.Length > EmailMax || !EmailValidator.IsValid(row.Email)
            || string.IsNullOrWhiteSpace(row.GroupName) || row.GroupName.Length > GroupNameMax)
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
    }

    private static void MarkConflict(StudentImportRow row, string field, string code)
    {
        row.Action = ImportRowAction.Conflict;
        row.Errors.Add(new ImportRowError(field, code, null, row.RowNumber));
    }

    private static AppException IdentityFailure(IdentityResult result)
    {
        var password = result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal));
        if (password) return AppException.Validation("password", FieldCodes.PasswordWeak);
        var duplicate = result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName");
        if (duplicate) return AppException.Validation("email", ErrorCodes.Duplicate);
        return AppException.Validation("email", FieldCodes.Invalid);
    }

    private static ImportPreviewDto ToPreview(string importId, StudentImportPayload payload, IReadOnlyList<ImportCellError> fileErrors) =>
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
                    ["email"] = r.Email,
                    ["group"] = r.GroupName
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

    private sealed record GroupSnap(Guid Id, string Name, GroupStatus Status);
    private sealed record StudentSnap(Guid Id, string Email, Guid UserId);
}
