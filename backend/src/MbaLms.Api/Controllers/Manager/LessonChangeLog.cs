using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>Allow-listed lesson fields. Comment never stores full text — only "set" / "changed".</summary>
internal static class LessonChangeLog
{
    public const string CommentSet = "set";
    public const string CommentChanged = "changed";

    public sealed record Snapshot(
        string GroupName,
        string DisciplineName,
        string TeacherName,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        LessonFormat? Format,
        string? Location,
        string? Comment,
        LessonStatus Status);

    public static List<ChangeLogField> ForCreate(Snapshot s)
    {
        var changes = new List<ChangeLogField>
        {
            new("group", null, s.GroupName),
            new("discipline", null, s.DisciplineName),
            new("teacher", null, s.TeacherName),
            new("startsAt", null, FormatInstant(s.StartsAt)),
            new("endsAt", null, FormatInstant(s.EndsAt)),
            new("format", null, s.Format?.ToString()),
            new("location", null, s.Location),
            new("status", null, s.Status.ToString()),
        };
        if (s.Comment is not null)
            changes.Add(new("comment", null, CommentSet));
        return changes;
    }

    public static List<ChangeLogField> Diff(Snapshot old, Snapshot next)
    {
        var changes = new List<ChangeLogField>();
        if (!string.Equals(old.GroupName, next.GroupName, StringComparison.Ordinal))
            changes.Add(new("group", old.GroupName, next.GroupName));
        if (!string.Equals(old.DisciplineName, next.DisciplineName, StringComparison.Ordinal))
            changes.Add(new("discipline", old.DisciplineName, next.DisciplineName));
        if (!string.Equals(old.TeacherName, next.TeacherName, StringComparison.Ordinal))
            changes.Add(new("teacher", old.TeacherName, next.TeacherName));
        if (old.StartsAt != next.StartsAt)
            changes.Add(new("startsAt", FormatInstant(old.StartsAt), FormatInstant(next.StartsAt)));
        if (old.EndsAt != next.EndsAt)
            changes.Add(new("endsAt", FormatInstant(old.EndsAt), FormatInstant(next.EndsAt)));
        if (old.Format != next.Format)
            changes.Add(new("format", old.Format?.ToString(), next.Format?.ToString()));
        if (!string.Equals(old.Location, next.Location, StringComparison.Ordinal))
            changes.Add(new("location", old.Location, next.Location));
        if (!string.Equals(old.Comment, next.Comment, StringComparison.Ordinal))
            changes.Add(new("comment", null, CommentChanged));
        if (old.Status != next.Status)
            changes.Add(new("status", old.Status.ToString(), next.Status.ToString()));
        return changes;
    }

    public static List<ChangeLogField> ForDelete(Snapshot s) =>
    [
        new("group", s.GroupName, null),
        new("discipline", s.DisciplineName, null),
        new("teacher", s.TeacherName, null),
        new("startsAt", FormatInstant(s.StartsAt), null),
        new("endsAt", FormatInstant(s.EndsAt), null),
        new("format", s.Format?.ToString(), null),
        new("location", s.Location, null),
        new("status", s.Status.ToString(), null),
    ];

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, AppTime time, string? source = null) =>
        new(Title(s, time), [s.GroupName], actorEmail, source);

    public const string SourceExcelImport = "excelImport";

    public static Snapshot From(
        string groupName, string disciplineName, string teacherName,
        DateTimeOffset startsAt, DateTimeOffset endsAt,
        LessonFormat? format, string? location, string? comment, LessonStatus status) =>
        new(groupName, disciplineName, teacherName, startsAt, endsAt, format, location, comment, status);

    public static string Title(Snapshot s, AppTime time)
    {
        var local = time.ToLocal(s.StartsAt);
        return $"{s.DisciplineName} · {local:yyyy-MM-dd HH:mm}";
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
