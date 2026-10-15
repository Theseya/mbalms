using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>Builds allow-listed announcement change fields. Body content is never logged.</summary>
internal static class AnnouncementChangeLog
{
    public const string BodySet = "set";
    public const string BodyChanged = "changed";

    public static List<ChangeLogField> ForCreate(Announcement a, IReadOnlyList<string> groupNames) =>
    [
        new("title", null, a.Title),
        new("body", null, BodySet),
        new("groupIds", null, groupNames.ToList()),
        new("isPinned", null, a.IsPinned),
        new("expiresAt", null, FormatExpires(a.ExpiresAt)),
        new("status", null, a.Status.ToString()),
    ];

    public static List<ChangeLogField> Diff(
        string oldTitle, string newTitle,
        string oldBody, string newBody,
        IReadOnlyList<string> oldGroupNames, IReadOnlyList<string> newGroupNames,
        bool oldPinned, bool newPinned,
        DateTimeOffset? oldExpires, DateTimeOffset? newExpires,
        AnnouncementStatus oldStatus, AnnouncementStatus newStatus)
    {
        var changes = new List<ChangeLogField>();
        if (!string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            changes.Add(new("title", oldTitle, newTitle));
        if (!string.Equals(oldBody, newBody, StringComparison.Ordinal))
            changes.Add(new("body", null, BodyChanged));
        if (!NamesEqual(oldGroupNames, newGroupNames))
            changes.Add(new("groupIds", oldGroupNames.ToList(), newGroupNames.ToList()));
        if (oldPinned != newPinned)
            changes.Add(new("isPinned", oldPinned, newPinned));
        if (oldExpires != newExpires)
            changes.Add(new("expiresAt", FormatExpires(oldExpires), FormatExpires(newExpires)));
        if (oldStatus != newStatus)
            changes.Add(new("status", oldStatus.ToString(), newStatus.ToString()));
        return changes;
    }

    public static List<ChangeLogField> StatusOnly(AnnouncementStatus oldStatus, AnnouncementStatus newStatus) =>
        [new("status", oldStatus.ToString(), newStatus.ToString())];

    public static ChangeLogContext Context(string title, IReadOnlyList<string> groupNames, string? actorEmail) =>
        new(title, groupNames.ToList(), actorEmail);

    private static string? FormatExpires(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    private static bool NamesEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
