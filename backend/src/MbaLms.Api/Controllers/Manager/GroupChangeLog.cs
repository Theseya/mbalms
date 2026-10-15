using System.Globalization;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>Allow-listed group fields for the manager ChangeLog.</summary>
internal static class GroupChangeLog
{
    public const string SourceExcelImport = "excelImport";

    public sealed record Snapshot(string Name, DateOnly? StartDate, DateOnly? EndDate, GroupStatus Status);

    public static List<ChangeLogField> ForCreate(Snapshot s) =>
    [
        new("name", null, s.Name),
        new("startDate", null, FormatDate(s.StartDate)),
        new("endDate", null, FormatDate(s.EndDate)),
        new("status", null, s.Status.ToString()),
    ];

    public static List<ChangeLogField> Diff(Snapshot old, Snapshot next)
    {
        var changes = new List<ChangeLogField>();
        if (!string.Equals(old.Name, next.Name, StringComparison.Ordinal))
            changes.Add(new("name", old.Name, next.Name));
        if (old.StartDate != next.StartDate)
            changes.Add(new("startDate", FormatDate(old.StartDate), FormatDate(next.StartDate)));
        if (old.EndDate != next.EndDate)
            changes.Add(new("endDate", FormatDate(old.EndDate), FormatDate(next.EndDate)));
        if (old.Status != next.Status)
            changes.Add(new("status", old.Status.ToString(), next.Status.ToString()));
        return changes;
    }

    public static List<ChangeLogField> ForDelete(Snapshot s) =>
    [
        new("name", s.Name, null),
        new("startDate", FormatDate(s.StartDate), null),
        new("endDate", FormatDate(s.EndDate), null),
        new("status", s.Status.ToString(), null),
    ];

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(s.Name, [s.Name], actorEmail, source);

    public static Snapshot From(Group g) => new(g.Name, g.StartDate, g.EndDate, g.Status);

    private static string? FormatDate(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
