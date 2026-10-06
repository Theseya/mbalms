using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Controllers.Manager;

/// <summary>Shared validation and construction of survey / template questions.</summary>
public static class SurveyQuestionBuilder
{
    public const int ScaleLimit = 100;
    public const int MaxOptions = 20;

    public static SurveyQuestion BuildSurveyQuestion(SurveyQuestionRequest q, int order, string path)
    {
        var built = BuildFields(q, order, path);
        var question = new SurveyQuestion
        {
            Id = Guid.CreateVersion7(),
            Order = built.Order,
            Text = built.Text,
            Type = built.Type,
            IsRequired = built.IsRequired,
            ScaleMin = built.ScaleMin,
            ScaleMax = built.ScaleMax,
            ScaleMinLabel = built.ScaleMinLabel,
            ScaleMaxLabel = built.ScaleMaxLabel,
            Options = built.OptionTexts.Select((o, i) => new SurveyQuestionOption
            {
                Id = Guid.CreateVersion7(), Order = i, Text = o
            }).ToList()
        };
        return question;
    }

    public static SurveyTemplateQuestion BuildTemplateQuestion(SurveyQuestionRequest q, int order, string path)
    {
        var built = BuildFields(q, order, path);
        return new SurveyTemplateQuestion
        {
            Id = Guid.CreateVersion7(),
            Order = built.Order,
            Text = built.Text,
            Type = built.Type,
            IsRequired = built.IsRequired,
            ScaleMin = built.ScaleMin,
            ScaleMax = built.ScaleMax,
            ScaleMinLabel = built.ScaleMinLabel,
            ScaleMaxLabel = built.ScaleMaxLabel,
            Options = built.OptionTexts.Select((o, i) => new SurveyTemplateOption
            {
                Id = Guid.CreateVersion7(), Order = i, Text = o
            }).ToList()
        };
    }

    public static SurveyQuestionRequest ToRequest(SurveyQuestion q) =>
        new(q.Text, q.Type, q.IsRequired, q.ScaleMin, q.ScaleMax,
            q.Type == QuestionType.SingleChoice
                ? q.Options.OrderBy(o => o.Order).Select(o => o.Text).ToList()
                : null,
            q.ScaleMinLabel, q.ScaleMaxLabel);

    public static SurveyQuestionRequest ToRequest(SurveyTemplateQuestion q) =>
        new(q.Text, q.Type, q.IsRequired, q.ScaleMin, q.ScaleMax,
            q.Type == QuestionType.SingleChoice
                ? q.Options.OrderBy(o => o.Order).Select(o => o.Text).ToList()
                : null,
            q.ScaleMinLabel, q.ScaleMaxLabel);

    private static Built BuildFields(SurveyQuestionRequest q, int order, string path)
    {
        if (string.IsNullOrWhiteSpace(q.Text)) throw AppException.Validation($"{path}.text", FieldCodes.Required);
        var type = q.Type ?? throw AppException.Validation($"{path}.type", FieldCodes.Required);
        int? scaleMin = null, scaleMax = null;
        string? scaleMinLabel = null, scaleMaxLabel = null;
        var optionTexts = new List<string>();

        switch (type)
        {
            case QuestionType.Scale:
                if (q.ScaleMin is null) throw AppException.Validation($"{path}.scaleMin", FieldCodes.Required);
                if (q.ScaleMax is null) throw AppException.Validation($"{path}.scaleMax", FieldCodes.Required);
                if (q.ScaleMin < -ScaleLimit || q.ScaleMax > ScaleLimit || q.ScaleMin >= q.ScaleMax)
                    throw AppException.Validation($"{path}.scaleMax", FieldCodes.Range);
                scaleMin = q.ScaleMin;
                scaleMax = q.ScaleMax;
                scaleMinLabel = Clean(q.ScaleMinLabel);
                scaleMaxLabel = Clean(q.ScaleMaxLabel);
                break;
            case QuestionType.SingleChoice:
                optionTexts = (q.Options ?? []).Select(o => o?.Trim()).Where(o => !string.IsNullOrEmpty(o)).Cast<string>().ToList();
                if (optionTexts.Count < 2 || optionTexts.Count > MaxOptions)
                    throw AppException.Validation($"{path}.options", FieldCodes.Range);
                if (optionTexts.Any(o => o.Length > 500))
                    throw AppException.Validation($"{path}.options", FieldCodes.MaxLength);
                break;
            case QuestionType.Text:
                break;
            default:
                throw AppException.Validation($"{path}.type", FieldCodes.Range);
        }

        return new Built(order, q.Text.Trim(), type, q.IsRequired, scaleMin, scaleMax, scaleMinLabel, scaleMaxLabel, optionTexts);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Built(int Order, string Text, QuestionType Type, bool IsRequired,
        int? ScaleMin, int? ScaleMax, string? ScaleMinLabel, string? ScaleMaxLabel, List<string> OptionTexts);
}
