namespace MbaLms.Api.Domain;

/// <summary>
/// Rounding of the calculated weighted sum to the final grade. The only place that defines the rule:
/// change it here to change how every working sheet rounds. Only the total is rounded, never the addends.
/// </summary>
public static class FinalGradeRounding
{
    /// <summary>Half away from zero to a whole number: 7.49 → 7, 7.50 → 8.</summary>
    public static int Round(decimal raw) => (int)Math.Round(raw, 0, MidpointRounding.AwayFromZero);
}

public enum CalculationState
{
    Calculated,
    /// <summary>No category has a weight greater than 0%, or average mode has no included works and attendance is off.</summary>
    NoElements,
    /// <summary>The weights do not add up to exactly 100%.</summary>
    WeightsIncomplete,
    /// <summary>
    /// Weighted: a weighted category has an empty required work, no works, or attendance is not calculated.
    /// Average: an included work is empty, or attendance is flagged in and not calculated.
    /// An explicit 0 counts as filled in.
    /// </summary>
    MissingScores,
}

public record CalculationResult(CalculationState State, decimal? Raw, int? Rounded, int MissingCount);

/// <param name="Mode">Discipline-wide. Default (zero) is weighted.</param>
/// <param name="IncludeAttendanceInAverage">Average mode only; ignored when weighted.</param>
public readonly record struct CalculationContext(CalculationMode Mode, bool IncludeAttendanceInAverage);

public enum AttendanceStatus
{
    /// <summary>Total lessons, attended lessons or the discipline threshold is not set, or total is 0.</summary>
    Unknown,
    Met,
    NotMet,
}

public static class AssessmentCategoryNames
{
    public const string Attendance = "Посещаемость";

    /// <summary>
    /// UI warning only. Automatic attendance is <see cref="AssessmentCategoryKind.Attendance"/>, never this name.
    /// A manual category may be named this way and stays manual.
    /// </summary>
    public static bool LooksLikeAttendanceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        return trimmed.Equals(Attendance, StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("Attendance", StringComparison.OrdinalIgnoreCase);
    }
}

public static class WorkSheetRules
{
    public const decimal MaxScore = 10m;
    public const int MaxFinal = 10;
    public const decimal FullWeight = 100m;
    /// <summary>The final gradebook stores 0–100; a working-sheet final of 0–10 is multiplied by this factor.</summary>
    public const int GradebookFactor = 10;
    public const int MaxCategories = 12;
    public const int MaxWorksPerCategory = 15;

    public static decimal WeightTotal(IEnumerable<AssessmentCategory> categories) => categories.Sum(c => c.Weight);

    public static bool HasWeightedCategory(IEnumerable<AssessmentCategory> categories) => categories.Any(c => c.Weight > 0);

    /// <summary>Average of the works; null when the category has no works or a work is empty. Empty is not 0.</summary>
    public static decimal? CategoryScore(AssessmentCategory category, IReadOnlyDictionary<Guid, decimal> scores)
    {
        if (category.Kind != AssessmentCategoryKind.Manual) return null;
        var works = category.Works;
        if (works.Count == 0) return null;
        decimal sum = 0;
        foreach (var work in works)
        {
            if (!scores.TryGetValue(work.Id, out var value)) return null;
            sum += value;
        }
        return sum / works.Count;
    }

    /// <summary>
    /// Score on the 0–10 scale from attended / total × 10. Null when either value is missing or total is 0;
    /// that is not treated as 0.
    /// </summary>
    public static decimal? AttendanceScore(int? attended, int? total) =>
        attended is null || total is null or 0 ? null : attended.Value * MaxScore / total.Value;

    public static decimal Contribution(decimal score, decimal weight) => score * weight / FullWeight;

    public static bool TransferReady(IReadOnlyCollection<AssessmentCategory> categories, CalculationContext ctx) =>
        ctx.Mode == CalculationMode.Average
            ? AverageTerms(categories, ctx.IncludeAttendanceInAverage).Count > 0
            : HasWeightedCategory(categories) && WeightTotal(categories) == FullWeight;

    /// <summary>
    /// Weighted: Σ(category score × weight / 100); 0% categories are ignored for missing works.
    /// Average: unrounded mean of included works (weight ignored, including 0%) plus automatic attendance
    /// when flagged. Only the total is rounded. Every included work is required; an explicit 0 counts.
    /// </summary>
    public static CalculationResult Calculate(IReadOnlyCollection<AssessmentCategory> categories,
        IReadOnlyDictionary<Guid, decimal> scores, int? attended, int? totalLessons,
        CalculationContext ctx = default)
    {
        return ctx.Mode == CalculationMode.Average
            ? CalculateAverage(categories, scores, attended, totalLessons, ctx)
            : CalculateWeighted(categories, scores, attended, totalLessons);
    }

    static CalculationResult CalculateWeighted(IReadOnlyCollection<AssessmentCategory> categories,
        IReadOnlyDictionary<Guid, decimal> scores, int? attended, int? totalLessons)
    {
        var missing = 0;
        foreach (var category in categories.Where(c => c.Weight > 0))
        {
            if (category.Kind == AssessmentCategoryKind.Attendance)
            {
                if (AttendanceScore(attended, totalLessons) is null) missing++;
                continue;
            }
            if (category.Works.Count == 0)
            {
                missing++;
                continue;
            }
            missing += category.Works.Count(w => !scores.ContainsKey(w.Id));
        }

        if (!HasWeightedCategory(categories)) return new(CalculationState.NoElements, null, null, missing);
        if (WeightTotal(categories) != FullWeight) return new(CalculationState.WeightsIncomplete, null, null, missing);
        if (missing > 0) return new(CalculationState.MissingScores, null, null, missing);

        decimal raw = 0;
        foreach (var category in categories.Where(c => c.Weight > 0))
        {
            var score = category.Kind == AssessmentCategoryKind.Attendance
                ? AttendanceScore(attended, totalLessons)
                : CategoryScore(category, scores);
            raw += Contribution(score!.Value, category.Weight);
        }
        return new(CalculationState.Calculated, raw, FinalGradeRounding.Round(raw), 0);
    }

    static CalculationResult CalculateAverage(IReadOnlyCollection<AssessmentCategory> categories,
        IReadOnlyDictionary<Guid, decimal> scores, int? attended, int? totalLessons, CalculationContext ctx)
    {
        if (AverageTerms(categories, ctx.IncludeAttendanceInAverage).Count == 0)
            return new(CalculationState.NoElements, null, null, 0);

        var missing = 0;
        decimal sum = 0;
        var count = 0;
        foreach (var category in categories)
        {
            if (category.Kind != AssessmentCategoryKind.Manual || !category.IncludeInAverage) continue;
            foreach (var work in category.Works)
            {
                if (!scores.TryGetValue(work.Id, out var value)) { missing++; continue; }
                sum += value;
                count++;
            }
        }
        if (ctx.IncludeAttendanceInAverage)
        {
            var attendance = AttendanceScore(attended, totalLessons);
            if (attendance is null) missing++;
            else { sum += attendance.Value; count++; }
        }
        if (missing > 0) return new(CalculationState.MissingScores, null, null, missing);
        if (count == 0) return new(CalculationState.NoElements, null, null, 0);
        var raw = sum / count;
        return new(CalculationState.Calculated, raw, FinalGradeRounding.Round(raw), 0);
    }

    /// <summary>
    /// Names of scores in the unweighted mean: each included work, plus automatic attendance when flagged.
    /// Weight is ignored. Used by the UI composition list.
    /// </summary>
    public static IReadOnlyList<string> AverageTerms(IReadOnlyCollection<AssessmentCategory> categories,
        bool includeAttendance)
    {
        var names = new List<string>();
        foreach (var category in categories)
        {
            if (category.Kind != AssessmentCategoryKind.Manual || !category.IncludeInAverage) continue;
            foreach (var work in category.Works)
                names.Add(work.Name);
        }
        if (includeAttendance)
            names.Add(AssessmentCategoryNames.Attendance);
        return names;
    }

    /// <summary>
    /// Compared on the exact ratio attended / total against the threshold, not on a rounded percentage;
    /// exactly at the threshold counts as met.
    /// </summary>
    public static AttendanceStatus Attendance(int? attended, int? total, decimal? threshold)
    {
        if (attended is null || total is null or 0 || threshold is null) return AttendanceStatus.Unknown;
        return attended.Value * FullWeight >= threshold.Value * total.Value ? AttendanceStatus.Met : AttendanceStatus.NotMet;
    }

    /// <summary>Exact percentage for display; null when it cannot be computed.</summary>
    public static decimal? AttendancePercent(int? attended, int? total) =>
        attended is null || total is null or 0 ? null : attended.Value * FullWeight / total.Value;

    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;

    public static int? FinalOf(WorkSheetRow row) => row.ManualFinal ?? row.CalculatedRounded;
}
