using System.Security.Cryptography;
using System.Text;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>
/// Allow-listed survey-template fields. Never logs full question/option texts in the change payload
/// (structure changes are a marker + question count only).
/// </summary>
internal static class SurveyTemplateChangeLog
{
    public const string DescriptionSet = "set";
    public const string DescriptionChanged = "changed";
    public const string StructureChanged = "changed";
    public const string SourceFromSurvey = "fromSurvey";

    public sealed record Snapshot(
        SurveyType Type,
        string Title,
        string? Description,
        int QuestionCount,
        string QuestionsFingerprint);

    public static List<ChangeLogField> ForCreate(Snapshot s)
    {
        var changes = new List<ChangeLogField>
        {
            new("type", null, s.Type.ToString()),
            new("title", null, s.Title),
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
        if (!string.Equals(old.Description, next.Description, StringComparison.Ordinal))
            changes.Add(new("description", null, DescriptionChanged));
        if (old.QuestionCount != next.QuestionCount)
            changes.Add(new("questionCount", old.QuestionCount, next.QuestionCount));
        if (!string.Equals(old.QuestionsFingerprint, next.QuestionsFingerprint, StringComparison.Ordinal))
            changes.Add(new("questionsStructure", null, StructureChanged));
        return changes;
    }

    public static List<ChangeLogField> ForDelete(Snapshot s) =>
    [
        new("type", s.Type.ToString(), null),
        new("title", s.Title, null),
        new("questionCount", s.QuestionCount, null),
    ];

    public static ChangeLogContext Context(Snapshot s, string? actorEmail, string? source = null) =>
        new(s.Title, [], actorEmail, source);

    public static Snapshot SnapshotFrom(SurveyTemplate template) =>
        new(template.Type, template.Title, template.Description, template.Questions.Count,
            FingerprintQuestions(template.Questions));

    public static string FingerprintQuestions(IEnumerable<SurveyTemplateQuestion> questions)
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
}
