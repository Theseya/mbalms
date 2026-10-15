using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed grade fields for the manager ChangeLog.
/// Numeric scores are never stored — only value markers "set" / "changed".
/// Detailed value history remains in GradeHistory.
/// </summary>
internal static class GradeChangeLog
{
    public const string ValueSet = "set";
    public const string ValueChanged = "changed";
    public const string SourceExcelImport = "excelImport";

    public sealed record Snapshot(
        string StudentName,
        string GroupName,
        string DisciplineName,
        string PeriodName,
        GradeStatus Status);

    public static List<ChangeLogField> ForCreate(Snapshot s) =>
    [
        new("student", null, s.StudentName),
        new("group", null, s.GroupName),
        new("discipline", null, s.DisciplineName),
        new("period", null, s.PeriodName),
        new("status", null, s.Status.ToString()),
        new("value", null, ValueSet),
    ];

    public static List<ChangeLogField> ForValueUpdate() =>
    [
        new("value", null, ValueChanged),
    ];

    public static List<ChangeLogField> StatusOnly(GradeStatus oldStatus, GradeStatus newStatus) =>
    [
        new("status", oldStatus.ToString(), newStatus.ToString()),
    ];

    public static List<ChangeLogField> ForDelete(Snapshot s) =>
    [
        new("student", s.StudentName, null),
        new("group", s.GroupName, null),
        new("discipline", s.DisciplineName, null),
        new("period", s.PeriodName, null),
        new("status", s.Status.ToString(), null),
    ];

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(Title(s), [s.GroupName], actorEmail, source);

    public static string Title(Snapshot s) =>
        $"{s.StudentName} · {s.DisciplineName} · {s.PeriodName}";

    public static Snapshot From(Grade grade) =>
        new(
            grade.Student!.FullName,
            grade.Student.Group!.Name,
            grade.Discipline!.Name,
            grade.Period!.Name,
            grade.Status);
}
