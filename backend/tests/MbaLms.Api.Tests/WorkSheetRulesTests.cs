using MbaLms.Api.Domain;

namespace MbaLms.Api.Tests;

public class WorkSheetRulesTests
{
    private static AssessmentElement Work(string name) => new() { Id = Guid.NewGuid(), Name = name };

    private static AssessmentCategory Manual(decimal weight, params AssessmentElement[] works) => new()
    {
        Id = Guid.NewGuid(), Name = works.Length == 1 ? works[0].Name : "C", Weight = weight,
        Kind = AssessmentCategoryKind.Manual, Works = [.. works]
    };

    private static AssessmentCategory Attendance(decimal weight) => new()
    {
        Id = Guid.NewGuid(), Name = AssessmentCategoryNames.Attendance, Weight = weight,
        Kind = AssessmentCategoryKind.Attendance
    };

    private static Dictionary<Guid, decimal> Scores(params (AssessmentElement Work, decimal Value)[] items) =>
        items.ToDictionary(x => x.Work.Id, x => x.Value);

    [Theory]
    [InlineData("7.49", 7)]
    [InlineData("7.50", 8)]
    [InlineData("7.499999", 7)]
    [InlineData("0", 0)]
    [InlineData("0.5", 1)]
    [InlineData("9.5", 10)]
    [InlineData("10", 10)]
    public void Rounding_is_half_up_to_an_integer(string raw, int expected) =>
        Assert.Equal(expected, FinalGradeRounding.Round(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Category_score_is_the_unrounded_average_of_its_works()
    {
        var a = Work("L1");
        var b = Work("L2");
        var c = Work("L3");
        var lectures = Manual(20, a, b, c);
        Assert.Equal(8m, WorkSheetRules.CategoryScore(lectures, Scores((a, 8), (b, 6), (c, 10))));
        Assert.Null(WorkSheetRules.CategoryScore(lectures, Scores((a, 8), (b, 6))));
        Assert.Equal(0m, WorkSheetRules.CategoryScore(lectures, Scores((a, 0), (b, 0), (c, 0))));
    }

    [Fact]
    public void Total_sums_category_contributions_and_rounds_only_the_total()
    {
        var l1 = Work("L1");
        var l2 = Work("L2");
        var l3 = Work("L3");
        var s1 = Work("S1");
        var s2 = Work("S2");
        var exam = Work("KR");
        var result = WorkSheetRules.Calculate(
            [Manual(20, l1, l2, l3), Manual(30, s1, s2), Manual(30, exam), Attendance(20)],
            Scores((l1, 8), (l2, 6), (l3, 10), (s1, 7), (s2, 9), (exam, 9)),
            attended: 6, totalLessons: 10);
        Assert.Equal(CalculationState.Calculated, result.State);
        Assert.Equal(7.9m, result.Raw);
        Assert.Equal(8, result.Rounded);
    }

    [Fact]
    public void Rounding_applies_to_the_total_only()
    {
        var a = Work("A");
        var b = Work("B");
        var result = WorkSheetRules.Calculate([Manual(50, a), Manual(50, b)],
            Scores((a, 7.4m), (b, 7.6m)), null, null);
        Assert.Equal(7.5m, result.Raw);
        Assert.Equal(8, result.Rounded);
    }

    [Fact]
    public void Empty_score_is_not_zero_and_blocks_a_weighted_category()
    {
        var a = Work("A");
        var b = Work("B");
        var missing = WorkSheetRules.Calculate([Manual(40, a), Manual(60, b)], Scores((a, 9)), null, null);
        Assert.Equal(CalculationState.MissingScores, missing.State);
        Assert.Equal(1, missing.MissingCount);
        Assert.Null(missing.Raw);

        var zero = WorkSheetRules.Calculate([Manual(40, a), Manual(60, b)], Scores((a, 9), (b, 0)), null, null);
        Assert.Equal(CalculationState.Calculated, zero.State);
        Assert.Equal(3.6m, zero.Raw);
        Assert.Equal(4, zero.Rounded);
    }

    [Fact]
    public void Zero_weight_category_does_not_block_when_its_works_or_attendance_are_empty()
    {
        var required = Work("Exam");
        var unused = Work("Draft");
        var result = WorkSheetRules.Calculate(
            [Manual(100, required), Manual(0, unused), Attendance(0)],
            Scores((required, 8)), attended: null, totalLessons: null);
        Assert.Equal(CalculationState.Calculated, result.State);
        Assert.Equal(8m, result.Raw);
        Assert.Equal(8, result.Rounded);
    }

    [Fact]
    public void Weighted_category_without_works_is_missing()
    {
        var result = WorkSheetRules.Calculate([Manual(100)], new Dictionary<Guid, decimal>(), null, null);
        Assert.Equal(CalculationState.MissingScores, result.State);
        Assert.Equal(1, result.MissingCount);
    }

    [Fact]
    public void Attendance_score_is_attended_over_total_times_ten()
    {
        Assert.Equal(6m, WorkSheetRules.AttendanceScore(6, 10));
        Assert.Equal(0m, WorkSheetRules.AttendanceScore(0, 10));
        Assert.Equal(10m, WorkSheetRules.AttendanceScore(10, 10));
        Assert.Equal(10m / 3m, WorkSheetRules.AttendanceScore(1, 3));
        Assert.Null(WorkSheetRules.AttendanceScore(null, 10));
        Assert.Null(WorkSheetRules.AttendanceScore(6, null));
        Assert.Null(WorkSheetRules.AttendanceScore(6, 0));
        Assert.Equal(1.2m, WorkSheetRules.Contribution(6m, 20m));
    }

    [Fact]
    public void Weighted_attendance_without_data_blocks_the_total_not_as_zero()
    {
        var exam = Work("Exam");
        var cats = new[] { Manual(80, exam), Attendance(20) };
        var filled = Scores((exam, 10));
        Assert.Equal(CalculationState.MissingScores, WorkSheetRules.Calculate(cats, filled, null, 10).State);
        Assert.Equal(CalculationState.MissingScores, WorkSheetRules.Calculate(cats, filled, 6, null).State);
        Assert.Equal(CalculationState.MissingScores, WorkSheetRules.Calculate(cats, filled, 6, 0).State);

        var ready = WorkSheetRules.Calculate(cats, filled, 6, 10);
        Assert.Equal(CalculationState.Calculated, ready.State);
        Assert.Equal(9.2m, ready.Raw);
        Assert.Equal(9, ready.Rounded);
    }

    [Fact]
    public void Attendance_threshold_is_independent_of_the_score()
    {
        Assert.Equal(AttendanceStatus.NotMet, WorkSheetRules.Attendance(6, 10, 80));
        Assert.Equal(6m, WorkSheetRules.AttendanceScore(6, 10));
    }

    [Fact]
    public void No_result_without_weighted_categories_or_with_weights_not_adding_up_to_100()
    {
        Assert.Equal(CalculationState.NoElements, WorkSheetRules.Calculate([Attendance(0)], new Dictionary<Guid, decimal>(), null, null).State);
        var a = Work("A");
        var b = Work("B");
        var result = WorkSheetRules.Calculate([Manual(40, a), Manual(59.99m, b)],
            Scores((a, 10), (b, 10)), null, null);
        Assert.Equal(CalculationState.WeightsIncomplete, result.State);
        Assert.Null(result.Rounded);
    }

    [Theory]
    [InlineData(7, 10, "80", AttendanceStatus.NotMet)]
    [InlineData(8, 10, "80", AttendanceStatus.Met)]
    [InlineData(9, 10, "80", AttendanceStatus.Met)]
    [InlineData(1, 3, "33.33", AttendanceStatus.Met)]
    [InlineData(2, 3, "66.67", AttendanceStatus.NotMet)]
    [InlineData(0, 10, "0", AttendanceStatus.Met)]
    [InlineData(10, 10, "100", AttendanceStatus.Met)]
    [InlineData(0, 0, "80", AttendanceStatus.Unknown)]
    public void Attendance_status_uses_the_exact_ratio(int attended, int total, string threshold, AttendanceStatus expected) =>
        Assert.Equal(expected, WorkSheetRules.Attendance(attended, total,
            decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Attendance_is_unknown_without_data()
    {
        Assert.Equal(AttendanceStatus.Unknown, WorkSheetRules.Attendance(null, 10, 80));
        Assert.Equal(AttendanceStatus.Unknown, WorkSheetRules.Attendance(5, null, 80));
        Assert.Equal(AttendanceStatus.Unknown, WorkSheetRules.Attendance(5, 10, null));
        Assert.Null(WorkSheetRules.AttendancePercent(0, 0));
        Assert.Equal(80m, WorkSheetRules.AttendancePercent(8, 10));
    }

    [Theory]
    [InlineData("посещаемость")]
    [InlineData("Посещаемость")]
    [InlineData("ПОСЕЩАЕМОСТЬ")]
    [InlineData("Attendance")]
    public void Manual_category_named_like_attendance_uses_work_scores_not_the_attendance_formula(string name)
    {
        var work = Work("Работа");
        var manual = new AssessmentCategory
        {
            Id = Guid.NewGuid(), Name = name, Weight = 100, Kind = AssessmentCategoryKind.Manual, Works = [work]
        };
        var auto = new AssessmentCategory
        {
            Id = Guid.NewGuid(), Name = "Other", Weight = 0, Kind = AssessmentCategoryKind.Attendance
        };
        var result = WorkSheetRules.Calculate([manual, auto], Scores((work, 7)), 1, 10);
        Assert.Equal(CalculationState.Calculated, result.State);
        Assert.Equal(7m, result.Raw);
        Assert.Equal(7m, WorkSheetRules.CategoryScore(manual, Scores((work, 7))));
    }

    [Fact]
    public void Attendance_kind_uses_the_formula_regardless_of_stored_name()
    {
        var auto = new AssessmentCategory
        {
            Id = Guid.NewGuid(), Name = "Не посещаемость", Weight = 100, Kind = AssessmentCategoryKind.Attendance
        };
        var result = WorkSheetRules.Calculate([auto], new Dictionary<Guid, decimal>(), 6, 10);
        Assert.Equal(CalculationState.Calculated, result.State);
        Assert.Equal(6m, result.Raw);
    }

    [Theory]
    [InlineData("7.55", true)]
    [InlineData("7.5", true)]
    [InlineData("7", true)]
    [InlineData("7.555", false)]
    [InlineData("0.001", false)]
    public void At_most_two_decimals(string value, bool expected) =>
        Assert.Equal(expected, WorkSheetRules.HasAtMostTwoDecimals(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    private static CalculationContext Average(bool includeAttendance = false) =>
        new(CalculationMode.Average, includeAttendance);

    [Fact]
    public void Average_is_the_unrounded_mean_of_works_and_ignores_weight_including_zero()
    {
        var l1 = Work("L1");
        var l2 = Work("L2");
        var unused = Work("Draft");
        var exam = Work("KR");
        var cats = new[] { Manual(20, l1, l2), Manual(0, unused), Manual(80, exam), Attendance(0) };
        var scores = Scores((l1, 8), (l2, 6), (unused, 10), (exam, 9));
        var result = WorkSheetRules.Calculate(cats, scores, 6, 10, Average());
        Assert.Equal(CalculationState.Calculated, result.State);
        Assert.Equal(8.25m, result.Raw);
        Assert.Equal(8, result.Rounded);
        Assert.Equal(["L1", "L2", "Draft", "KR"], WorkSheetRules.AverageTerms(cats, false));
    }

    [Fact]
    public void Average_include_flag_off_omits_works_and_empty_scores_do_not_block()
    {
        var kept = Work("Kept");
        var omitted = Work("Omitted");
        var omittedEmpty = Work("Empty");
        var included = Manual(50, kept);
        var excluded = Manual(50, omitted, omittedEmpty);
        excluded.IncludeInAverage = false;
        var cats = new[] { included, excluded, Attendance(0) };
        var result = WorkSheetRules.Calculate(cats, Scores((kept, 7)), null, null, Average());
        Assert.Equal(CalculationState.Calculated, result.State);
        Assert.Equal(7m, result.Raw);
        Assert.Equal(["Kept"], WorkSheetRules.AverageTerms(cats, false));
    }

    [Fact]
    public void Average_attendance_flag_adds_one_score_and_is_not_used_when_weighted()
    {
        var exam = Work("Exam");
        var cats = new[] { Manual(100, exam), Attendance(0) };
        var scores = Scores((exam, 10));
        var withAttendance = WorkSheetRules.Calculate(cats, scores, 6, 10, Average(includeAttendance: true));
        Assert.Equal(8m, withAttendance.Raw);
        Assert.Equal(8, withAttendance.Rounded);

        var weighted = WorkSheetRules.Calculate(cats, scores, 6, 10, new(CalculationMode.Weighted, true));
        Assert.Equal(10m, weighted.Raw);
    }

    [Fact]
    public void Average_with_no_included_works_and_attendance_off_is_no_data_not_zero()
    {
        var work = Work("A");
        var excluded = Manual(100, work);
        excluded.IncludeInAverage = false;
        var cats = new[] { excluded, Attendance(0) };
        var result = WorkSheetRules.Calculate(cats, Scores((work, 9)), 10, 10, Average());
        Assert.Equal(CalculationState.NoElements, result.State);
        Assert.Null(result.Raw);
        Assert.Null(result.Rounded);
        Assert.Empty(WorkSheetRules.AverageTerms(cats, false));
    }

    [Fact]
    public void Average_missing_included_work_or_flagged_attendance_blocks_not_as_zero()
    {
        var a = Work("A");
        var b = Work("B");
        var cats = new[] { Manual(40, a), Manual(60, b), Attendance(0) };
        var missingWork = WorkSheetRules.Calculate(cats, Scores((a, 8)), null, null, Average());
        Assert.Equal(CalculationState.MissingScores, missingWork.State);
        Assert.Equal(1, missingWork.MissingCount);
        Assert.Null(missingWork.Raw);

        var missingAttendance = WorkSheetRules.Calculate(cats, Scores((a, 8), (b, 9)), null, 10, Average(true));
        Assert.Equal(CalculationState.MissingScores, missingAttendance.State);
        Assert.Null(missingAttendance.Raw);

        var zero = WorkSheetRules.Calculate(cats, Scores((a, 0), (b, 0)), 0, 10, Average(true));
        Assert.Equal(CalculationState.Calculated, zero.State);
        Assert.Equal(0m, zero.Raw);
    }
}
