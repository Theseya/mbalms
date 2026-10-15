using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed working-sheet fields. As for final grades, scores and the manual final grade are never stored —
/// only the markers set / changed / cleared. One entry per changed student row, never the whole sheet.
/// </summary>
internal static class WorkSheetChangeLog
{
    public const string Set = "set";
    public const string Changed = "changed";
    public const string Cleared = "cleared";
    public const string SourceWorkSheet = "workSheet";
    /// <summary>Field prefix of a score; the element name follows, e.g. "score:Экзамен".</summary>
    public const string ScorePrefix = "score:";

    public static ChangeLogField? Marker(string field, bool hadValue, bool hasValue, bool valueChanged)
    {
        if (!hadValue && hasValue) return new(field, null, Set);
        if (hadValue && !hasValue) return new(field, null, Cleared);
        return hadValue && valueChanged ? new(field, null, Changed) : null;
    }

    public static ChangeLogContext RowContext(string studentName, string disciplineName, string groupName, string periodName,
        string? actorEmail) =>
        new($"{studentName} · {disciplineName} · {periodName}", [groupName], actorEmail);

    public static ChangeLogContext SheetContext(string disciplineName, string groupName, string periodName, string? actorEmail) =>
        new($"{disciplineName} · {groupName} · {periodName}", [groupName], actorEmail);

    public static ChangeLogContext ElementContext(string elementName, string disciplineName, string? actorEmail) =>
        new($"{disciplineName} · {elementName}", [], actorEmail);
}
