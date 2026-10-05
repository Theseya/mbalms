using MbaLms.Api.Domain;

namespace MbaLms.Api.Infrastructure.Import;

public enum ImportRowAction
{
    Create,
    Update,
    Conflict,
    Error,
    Skip
}

public sealed record ImportCellError(string Sheet, int Row, string Column, string Code, string? Field = null);

public sealed record ImportRowError(string Field, string Code, string? Sheet = null, int? Row = null, string? Column = null);

/// <summary>Non-blocking preview note (e.g. schedule overlap).</summary>
public sealed record ImportRowWarning(string Code, string? Message = null);

public sealed record ImportPreviewRowDto(
    int RowNumber,
    ImportRowAction Action,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyList<ImportRowWarning>? Warnings = null);

public sealed record ImportPreviewDto(
    string ImportId,
    int CreateCount,
    int UpdateCount,
    int ConflictCount,
    int ErrorCount,
    IReadOnlyList<ImportPreviewRowDto> Rows,
    IReadOnlyList<ImportCellError> FileErrors);

public sealed record ImportConfirmRequest(string ImportId);

public sealed record ImportConfirmResultDto(int Created, int Updated, int Skipped);

public sealed class ImportSession
{
    public required string ImportId { get; init; }
    public required Guid ManagerUserId { get; init; }
    public required string Entity { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required object Payload { get; init; }
}

/// <summary>Parsed discipline row stored between preview and confirm (no secrets).</summary>
public sealed class DisciplineImportRow
{
    public int RowNumber { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public ImportRowAction Action { get; set; }
    public Guid? MatchedId { get; set; }
    public List<ImportRowError> Errors { get; init; } = [];
}

public sealed class DisciplineImportPayload
{
    public List<DisciplineImportRow> Rows { get; init; } = [];
}

/// <summary>Parsed teacher row stored between preview and confirm.</summary>
public sealed class TeacherImportRow
{
    public int RowNumber { get; init; }
    public required string LastName { get; init; }
    public required string FirstName { get; init; }
    public string? MiddleName { get; init; }
    public string? Email { get; init; }
    public ImportRowAction Action { get; set; }
    public Guid? MatchedId { get; set; }
    public List<ImportRowError> Errors { get; init; } = [];
}

public sealed class TeacherImportPayload
{
    public List<TeacherImportRow> Rows { get; init; } = [];
}

/// <summary>Parsed group row stored between preview and confirm. Status/ArchivedAt are never imported.</summary>
public sealed class GroupImportRow
{
    public int RowNumber { get; init; }
    public required string Name { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public ImportRowAction Action { get; set; }
    public Guid? MatchedId { get; set; }
    public List<ImportRowError> Errors { get; init; } = [];
}

public sealed class GroupImportPayload
{
    public List<GroupImportRow> Rows { get; init; } = [];
}

/// <summary>Parsed student row between preview and confirm. Passwords are never stored here.</summary>
public sealed class StudentImportRow
{
    public int RowNumber { get; init; }
    public required string LastName { get; init; }
    public required string FirstName { get; init; }
    public string? MiddleName { get; init; }
    public required string Email { get; init; }
    public required string GroupName { get; init; }
    public Guid? MatchedStudentId { get; set; }
    public Guid? ResolvedGroupId { get; set; }
    public ImportRowAction Action { get; set; }
    public List<ImportRowError> Errors { get; init; } = [];
}

public sealed class StudentImportPayload
{
    public List<StudentImportRow> Rows { get; init; } = [];
}

public sealed record StudentImportPasswordEntry(int RowNumber, string Password);

public sealed record StudentImportConfirmRequest(string ImportId, IReadOnlyList<StudentImportPasswordEntry>? Passwords);

/// <summary>Parsed schedule row between preview and confirm.</summary>
public sealed class LessonImportRow
{
    public int RowNumber { get; init; }
    public Guid? LessonId { get; init; }
    public required string DateText { get; init; }
    public required string StartText { get; init; }
    public required string EndText { get; init; }
    public required string GroupName { get; init; }
    public required string DisciplineName { get; init; }
    public required string TeacherName { get; init; }
    public string? FormatText { get; init; }
    public string? Location { get; init; }
    public string? Comment { get; init; }
    public string? StatusText { get; init; }

    public DateTime? StartsAtLocal { get; set; }
    public DateTime? EndsAtLocal { get; set; }
    public Guid? ResolvedGroupId { get; set; }
    public Guid? ResolvedDisciplineId { get; set; }
    public Guid? ResolvedTeacherId { get; set; }
    public LessonFormat? Format { get; set; }
    public LessonStatus Status { get; set; } = LessonStatus.Scheduled;

    public ImportRowAction Action { get; set; }
    public List<ImportRowError> Errors { get; init; } = [];
    public List<ImportRowWarning> Warnings { get; init; } = [];
}

public sealed class LessonImportPayload
{
    public List<LessonImportRow> Rows { get; init; } = [];
}

/// <summary>Parsed grade row between preview and confirm. Status/PublishedAt/password are never imported.</summary>
public sealed class GradeImportRow
{
    public int RowNumber { get; init; }
    public required string Email { get; init; }
    public required string DisciplineName { get; init; }
    public required string PeriodName { get; init; }
    public int Value { get; init; }

    public Guid? MatchedGradeId { get; set; }
    public Guid? ResolvedStudentId { get; set; }
    public Guid? ResolvedDisciplineId { get; set; }
    public Guid? ResolvedPeriodId { get; set; }

    public ImportRowAction Action { get; set; }
    public List<ImportRowError> Errors { get; init; } = [];
}

public sealed class GradeImportPayload
{
    public List<GradeImportRow> Rows { get; init; } = [];
}
