using System.Security.Cryptography;
using System.Text;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed survey fields for the manager change log.
/// Never logs question/option texts, answers, or response payloads.
/// </summary>
internal static class SurveyChangeLog
{
    public const string DescriptionSet = "set";
    public const string DescriptionChanged = "changed";
    public const string StructureChanged = "changed";
    public const string SourceFromTemplate = "fromTemplate";

    public sealed record Snapshot(
        SurveyType Type,
        string Title,
        string? Description,
        string GroupName,
        string? TeacherName,
        string? DisciplineName,
        DateTimeOffset? OpensAt,
        DateTimeOffset? ClosesAt,
        SurveyStatus Status,
        int QuestionCount,
        string QuestionsFingerprint);

    public static List<ChangeLogField> ForCreate(Snapshot s)
    {
        var changes = new List<ChangeLogField>
        {
            new("type", null, s.Type.ToString()),
            new("title", null, s.Title),
            new("group", null, s.GroupName),
            new("teacher", null, s.TeacherName),
            new("discipline", null, s.DisciplineName),
            new("opensAt", null, FormatInstant(s.OpensAt)),
            new("closesAt", null, FormatInstant(s.ClosesAt)),
            new("status", null, s.Status.ToString()),
            new("questionCount", null, s.QuestionCount),
        };
        if (s.Description is not null)
            changes.Add(new("description", null, DescriptionSet));
        return changes;
    }

    public static List<ChangeLogField> Diff(Snapshot old, Snapshot next)
    {
        var changes = new List<ChangeLogField>();
        if (old.Type != next.Type)
            changes.Add(new("type", old.Type.ToString(), next.Type.ToString()));
        if (!string.Equals(old.Title, next.Title, StringComparison.Ordinal))
            changes.Add(new("title", old.Title, next.Title));
        if (!string.Equals(old.GroupName, next.GroupName, StringComparison.Ordinal))
            changes.Add(new("group", old.GroupName, next.GroupName));
        if (!string.Equals(old.TeacherName, next.TeacherName, StringComparison.Ordinal))
            changes.Add(new("teacher", old.TeacherName, next.TeacherName));
        if (!string.Equals(old.DisciplineName, next.DisciplineName, StringComparison.Ordinal))
            changes.Add(new("discipline", old.DisciplineName, next.DisciplineName));
        if (old.OpensAt != next.OpensAt)
            changes.Add(new("opensAt", FormatInstant(old.OpensAt), FormatInstant(next.OpensAt)));
        if (old.ClosesAt != next.ClosesAt)
            changes.Add(new("closesAt", FormatInstant(old.ClosesAt), FormatInstant(next.ClosesAt)));
        if (old.Status != next.Status)
            changes.Add(new("status", old.Status.ToString(), next.Status.ToString()));
        if (!string.Equals(old.Description, next.Description, StringComparison.Ordinal))
            changes.Add(new("description", null, DescriptionChanged));
        if (old.QuestionCount != next.QuestionCount)
            changes.Add(new("questionCount", old.QuestionCount, next.QuestionCount));
        if (!string.Equals(old.QuestionsFingerprint, next.QuestionsFingerprint, StringComparison.Ordinal))
            changes.Add(new("questionsStructure", null, StructureChanged));
        return changes;
    }

    public static List<ChangeLogField> StatusOnly(SurveyStatus oldStatus, SurveyStatus newStatus) =>
        [new("status", oldStatus.ToString(), newStatus.ToString())];

    public static List<ChangeLogField> ForDelete(Snapshot s) =>
    [
        new("type", s.Type.ToString(), null),
        new("title", s.Title, null),
        new("group", s.GroupName, null),
        new("teacher", s.TeacherName, null),
        new("discipline", s.DisciplineName, null),
        new("opensAt", FormatInstant(s.OpensAt), null),
        new("closesAt", FormatInstant(s.ClosesAt), null),
        new("status", s.Status.ToString(), null),
        new("questionCount", s.QuestionCount, null),
    ];

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(s.Title, [s.GroupName], actorEmail, source);

    public static string FingerprintQuestions(IEnumerable<SurveyQuestion> questions)
    {
        var parts = questions.OrderBy(q => q.Order).Select(q =>
        {
            var opts = string.Join('\u001f', q.Options.OrderBy(o => o.Order).Select(o => o.Text));
            return string.Join('\u001e',
                q.Order.ToString(), q.Type.ToString(), q.IsRequired ? "1" : "0",
                q.Text, q.ScaleMin?.ToString() ?? "", q.ScaleMax?.ToString() ?? "",
                q.ScaleMinLabel ?? "", q.ScaleMaxLabel ?? "", opts);
        });
        var raw = string.Join('\u001d', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private static string? FormatInstant(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
