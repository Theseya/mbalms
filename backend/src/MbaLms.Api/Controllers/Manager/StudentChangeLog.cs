using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed student fields. Email/password never store secrets — markers only.
/// Email cleared is not an API scenario (email is required).
/// </summary>
internal static class StudentChangeLog
{
    public const string EmailSet = "set";
    public const string EmailChanged = "changed";
    public const string PasswordSet = "set";
    public const string PasswordReset = "reset";
    public const string SourceExcelImport = "excelImport";

    public sealed record Snapshot(
        string LastName, string FirstName, string? MiddleName,
        string Email, string GroupName);

    public static string FullName(Snapshot s) => string.IsNullOrWhiteSpace(s.MiddleName)
        ? $"{s.LastName} {s.FirstName}"
        : $"{s.LastName} {s.FirstName} {s.MiddleName}";

    public static List<ChangeLogField> ForCreate(Snapshot s, bool passwordSet)
    {
        var changes = new List<ChangeLogField>
        {
            new("lastName", null, s.LastName),
            new("firstName", null, s.FirstName),
        };
        if (s.MiddleName is not null)
            changes.Add(new("middleName", null, s.MiddleName));
        changes.Add(new("group", null, s.GroupName));
        changes.Add(new("email", null, EmailSet));
        if (passwordSet)
            changes.Add(new("password", null, PasswordSet));
        return changes;
    }

    public static List<ChangeLogField> Diff(Snapshot old, Snapshot next, bool passwordReset)
    {
        var changes = new List<ChangeLogField>();
        if (!string.Equals(old.LastName, next.LastName, StringComparison.Ordinal))
            changes.Add(new("lastName", old.LastName, next.LastName));
        if (!string.Equals(old.FirstName, next.FirstName, StringComparison.Ordinal))
            changes.Add(new("firstName", old.FirstName, next.FirstName));
        if (!string.Equals(old.MiddleName, next.MiddleName, StringComparison.Ordinal))
            changes.Add(new("middleName", old.MiddleName, next.MiddleName));
        if (!string.Equals(old.GroupName, next.GroupName, StringComparison.Ordinal))
            changes.Add(new("group", old.GroupName, next.GroupName));
        if (!string.Equals(old.Email, next.Email, StringComparison.OrdinalIgnoreCase))
        {
            // Email is required by API — only set (defensive) or changed; no cleared marker.
            if (string.IsNullOrWhiteSpace(old.Email) && !string.IsNullOrWhiteSpace(next.Email))
                changes.Add(new("email", null, EmailSet));
            else
                changes.Add(new("email", null, EmailChanged));
        }
        if (passwordReset)
            changes.Add(new("password", null, PasswordReset));
        return changes;
    }

    /// <summary>Delete snapshot: FIO + group only. No synthetic email/password changes.</summary>
    public static List<ChangeLogField> ForDelete(Snapshot s)
    {
        var changes = new List<ChangeLogField>
        {
            new("lastName", s.LastName, null),
            new("firstName", s.FirstName, null),
        };
        if (s.MiddleName is not null)
            changes.Add(new("middleName", s.MiddleName, null));
        changes.Add(new("group", s.GroupName, null));
        return changes;
    }

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(FullName(s), [s.GroupName], actorEmail, source);

    public static Snapshot From(Student s, string groupName) =>
        new(s.LastName, s.FirstName, s.MiddleName, s.Email, groupName);
}
