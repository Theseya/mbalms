using System.Globalization;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>Allow-listed academic period fields for the manager ChangeLog.</summary>
internal static class AcademicPeriodChangeLog
{
    public sealed record Snapshot(string Name, DateOnly? StartDate, DateOnly? EndDate);

    public static List<ChangeLogField> ForCreate(Snapshot s) =>
    [
        new("name", null, s.Name),
        new("startDate", null, FormatDate(s.StartDate)),
        new("endDate", null, FormatDate(s.EndDate)),
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
        return changes;
    }

    public static List<ChangeLogField> ForDelete(Snapshot s) =>
    [
        new("name", s.Name, null),
        new("startDate", FormatDate(s.StartDate), null),
        new("endDate", FormatDate(s.EndDate), null),
    ];

    public static ChangeLogContext Context(Snapshot s, string? actorEmail) =>
        new(s.Name, [], actorEmail);

    public static Snapshot From(AcademicPeriod p) => new(p.Name, p.StartDate, p.EndDate);

    private static string? FormatDate(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
