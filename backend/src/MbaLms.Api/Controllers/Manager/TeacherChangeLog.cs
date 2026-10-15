using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed teacher fields. Email never stores the address — only set / changed / cleared markers.
/// </summary>
internal static class TeacherChangeLog
{
    public const string EmailSet = "set";
    public const string EmailChanged = "changed";
    public const string EmailCleared = "cleared";
    public const string SourceExcelImport = "excelImport";

    public sealed record Snapshot(string LastName, string FirstName, string? MiddleName, string? Email);

    public static string FullName(Snapshot s) => string.IsNullOrWhiteSpace(s.MiddleName)
        ? $"{s.LastName} {s.FirstName}"
        : $"{s.LastName} {s.FirstName} {s.MiddleName}";

    public static List<ChangeLogField> ForCreate(Snapshot s)
    {
        var changes = new List<ChangeLogField>
        {
            new("lastName", null, s.LastName),
            new("firstName", null, s.FirstName),
        };
        if (s.MiddleName is not null)
            changes.Add(new("middleName", null, s.MiddleName));
        if (s.Email is not null)
            changes.Add(new("email", null, EmailSet));
        return changes;
    }

    public static List<ChangeLogField> Diff(Snapshot old, Snapshot next)
    {
        var changes = new List<ChangeLogField>();
        if (!string.Equals(old.LastName, next.LastName, StringComparison.Ordinal))
            changes.Add(new("lastName", old.LastName, next.LastName));
        if (!string.Equals(old.FirstName, next.FirstName, StringComparison.Ordinal))
            changes.Add(new("firstName", old.FirstName, next.FirstName));
        if (!string.Equals(old.MiddleName, next.MiddleName, StringComparison.Ordinal))
            changes.Add(new("middleName", old.MiddleName, next.MiddleName));
        if (!string.Equals(old.Email, next.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (old.Email is null && next.Email is not null)
                changes.Add(new("email", null, EmailSet));
            else if (old.Email is not null && next.Email is null)
                changes.Add(new("email", null, EmailCleared));
            else
                changes.Add(new("email", null, EmailChanged));
        }
        return changes;
    }

    public static List<ChangeLogField> ForDelete(Snapshot s)
    {
        var changes = new List<ChangeLogField>
        {
            new("lastName", s.LastName, null),
            new("firstName", s.FirstName, null),
        };
        if (s.MiddleName is not null)
            changes.Add(new("middleName", s.MiddleName, null));
        if (s.Email is not null)
            changes.Add(new("email", EmailSet, null));
        return changes;
    }

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(FullName(s), [], actorEmail, source);

    public static Snapshot From(Teacher t) => new(t.LastName, t.FirstName, t.MiddleName, t.Email);
}
