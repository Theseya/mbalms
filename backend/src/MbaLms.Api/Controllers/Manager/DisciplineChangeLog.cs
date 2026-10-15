using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed discipline fields. Description never stores full text — only set / changed / cleared.
/// Empty description on create does not emit a description marker.
/// </summary>
internal static class DisciplineChangeLog
{
    public const string DescriptionSet = "set";
    public const string DescriptionChanged = "changed";
    public const string DescriptionCleared = "cleared";
    public const string SourceExcelImport = "excelImport";

    public sealed record Snapshot(string Name, string? Description);

    public static List<ChangeLogField> ForCreate(Snapshot s)
    {
        var changes = new List<ChangeLogField> { new("name", null, s.Name) };
        if (s.Description is not null)
            changes.Add(new("description", null, DescriptionSet));
        return changes;
    }

    public static List<ChangeLogField> Diff(Snapshot old, Snapshot next)
    {
        var changes = new List<ChangeLogField>();
        if (!string.Equals(old.Name, next.Name, StringComparison.Ordinal))
            changes.Add(new("name", old.Name, next.Name));
        if (!string.Equals(old.Description, next.Description, StringComparison.Ordinal))
        {
            if (old.Description is null && next.Description is not null)
                changes.Add(new("description", null, DescriptionSet));
            else if (old.Description is not null && next.Description is null)
                changes.Add(new("description", null, DescriptionCleared));
            else
                changes.Add(new("description", null, DescriptionChanged));
        }
        return changes;
    }

    public static List<ChangeLogField> ForDelete(Snapshot s)
    {
        var changes = new List<ChangeLogField> { new("name", s.Name, null) };
        if (s.Description is not null)
            changes.Add(new("description", DescriptionSet, null));
        return changes;
    }

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(s.Name, [], actorEmail, source);

    public static Snapshot From(Discipline d) => new(d.Name, d.Description);
}
