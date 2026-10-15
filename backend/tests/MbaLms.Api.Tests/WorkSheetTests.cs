using System.Net;
using System.Text.Json;
using MbaLms.Api.Controllers;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class WorkSheetTests(ApiFactory factory) : TestBase(factory)
{
    private sealed record Seed(GroupDto Group, StudentAccount First, StudentAccount Second, DisciplineDto Discipline, PeriodDto Period)
    {
        public string Url => UrlFor(Period.Id);
        public string UrlFor(Guid periodId) => $"/api/manager/worksheets/{Group.Id}/{Discipline.Id}/{periodId}";
        public string SettingsUrl => $"/api/manager/worksheets/disciplines/{Discipline.Id}/settings";
    }

    private async Task<Seed> SeedAsync(ApiClient manager)
    {
        var group = await CreateGroupAsync(manager);
        var first = await CreateStudentAsync(manager, group.Id, "Алексеев");
        var second = await CreateStudentAsync(manager, group.Id, "Борисов");
        return new Seed(group, first, second, await CreateDisciplineAsync(manager), await CreatePeriodAsync(manager));
    }

    private static async Task<AssessmentSettingsDto> SetElementsAsync(ApiClient manager, Seed s, decimal? threshold,
        params (Guid? Id, string Name, decimal Weight)[] elements)
    {
        var current = await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl);
        var byWork = current.Categories.Where(c => c.Kind == AssessmentCategoryKind.Manual)
            .SelectMany(c => c.Works.Select(w => (Category: c, Work: w))).ToDictionary(x => x.Work.Id);
        var categories = elements.Select(e =>
        {
            Guid? categoryId = e.Id is Guid id && byWork.TryGetValue(id, out var hit) ? hit.Category.Id : null;
            return new
            {
                id = categoryId,
                name = e.Name,
                weight = e.Weight,
                works = new[] { new { id = e.Id, name = e.Name } },
            };
        }).ToArray();
        return await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceThreshold = threshold,
            attendanceWeight = current.AttendanceWeight,
            categories,
            confirmScoreRemoval = true,
        });
    }

    private static List<AssessmentWorkDto> ManualWorks(AssessmentSettingsDto settings) =>
        settings.Categories.Where(c => c.Kind == AssessmentCategoryKind.Manual).SelectMany(c => c.Works).ToList();

    private static IEnumerable<Guid> WorkIds(WorkSheetDto sheet) =>
        sheet.Categories.Where(c => c.Kind == AssessmentCategoryKind.Manual).SelectMany(c => c.Works).Select(w => w.Id);

    private static object Row(Guid studentId, Dictionary<Guid, decimal?>? scores = null, int? attended = null, int? manual = null) =>
        new { studentId, scores, attendedLessons = attended, manualFinal = manual };

    private static Task<WorkSheetDto> SaveRowsAsync(ApiClient manager, Seed s, params object[] rows) =>
        SaveRowsAsync(manager, s.Url, rows);

    private static Task<WorkSheetDto> SaveRowsAsync(ApiClient manager, string url, params object[] rows) =>
        manager.PutJsonAsync<WorkSheetDto>($"{url}/rows", new { rows });

    private static Task<WorkSheetDto> SaveSettingsAsync(ApiClient manager, Seed s, int? totalLessons) =>
        SaveSettingsAsync(manager, s.Url, totalLessons);

    private static Task<WorkSheetDto> SaveSettingsAsync(ApiClient manager, string url, int? totalLessons) =>
        manager.PutJsonAsync<WorkSheetDto>($"{url}/settings", new { totalLessons });

    private static Task<TransferResultDto> TransferAsync(ApiClient manager, string url, bool confirm,
        params (Guid StudentId, int Value)[] entries) =>
        manager.PostJsonAsync<TransferResultDto>($"{url}/transfer", new
        {
            entries = entries.Select(e => new { studentId = e.StudentId, value = e.Value }).ToArray(),
            confirmChanges = confirm,
        });

    private static WorkSheetRowDto RowOf(WorkSheetDto sheet, Guid studentId) => sheet.Rows.Single(r => r.StudentId == studentId);

    [Fact]
    public async Task Calculation_needs_full_weights_and_every_score_with_explicit_zero()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);

        var empty = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Null(empty.Id);
        Assert.Equal(2, empty.Rows.Count);
        Assert.All(empty.Rows, r => Assert.Equal(CalculationState.NoElements, r.CalculationState));

        // Draft: weights 40 + 50 are saved but nothing is calculated.
        var draft = await SetElementsAsync(manager, s, null, (null, "Кейс", 40), (null, "Экзамен", 50));
        Assert.False(draft.WeightsComplete);
        Assert.Equal(90, draft.WeightTotal);
        var caseId = ManualWorks(draft)[0].Id;
        var examId = ManualWorks(draft)[1].Id;
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [caseId] = 10, [examId] = 10 }));
        Assert.Equal(CalculationState.WeightsIncomplete, RowOf(sheet, s.First.Id).CalculationState);
        Assert.Null(RowOf(sheet, s.First.Id).Final);

        var full = await SetElementsAsync(manager, s, null, (caseId, "Кейс", 40), (examId, "Экзамен", 60));
        Assert.True(full.WeightsComplete);
        sheet = await SaveRowsAsync(manager, s,
            Row(s.First.Id, new() { [caseId] = 7.5m, [examId] = 7.4m }),
            Row(s.Second.Id, new() { [caseId] = 9, [examId] = null }));
        var first = RowOf(sheet, s.First.Id);
        Assert.Equal(CalculationState.Calculated, first.CalculationState);
        Assert.Equal(7.44m, first.CalculatedRaw);
        Assert.Equal(7, first.CalculatedRounded);
        Assert.Equal(7, first.Final);
        var second = RowOf(sheet, s.Second.Id);
        Assert.Equal(CalculationState.MissingScores, second.CalculationState);
        Assert.Equal(1, second.MissingCount);
        Assert.Null(second.Final);

        sheet = await SaveRowsAsync(manager, s, Row(s.Second.Id, new() { [caseId] = 9, [examId] = 0 }));
        second = RowOf(sheet, s.Second.Id);
        Assert.Equal(CalculationState.Calculated, second.CalculationState);
        Assert.Equal(0m, second.Scores[examId]);
        Assert.Equal(3.6m, second.CalculatedRaw);
        Assert.Equal(4, second.Final);
    }

    [Theory]
    [InlineData("7.49", 7)]
    [InlineData("7.5", 8)]
    public async Task Total_is_rounded_half_up(string score, int expected)
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Единственный", 100));
        var value = decimal.Parse(score, System.Globalization.CultureInfo.InvariantCulture);
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [ManualWorks(settings)[0].Id] = value }));
        Assert.Equal(value, RowOf(sheet, s.First.Id).CalculatedRaw);
        Assert.Equal(expected, RowOf(sheet, s.First.Id).Final);
    }

    [Fact]
    public async Task Ranges_and_decimals_are_validated()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var other = await CreateStudentAsync(manager, (await CreateGroupAsync(manager)).Id);

        var bad = await manager.PutAsync(s.SettingsUrl, new
        {
            attendanceThreshold = 100.01m,
            attendanceWeight = 0,
            categories = new object[]
            {
                new { name = "Тест", weight = 101, works = new[] { new { name = "Тест" } } },
                new { name = "", weight = 1, works = new[] { new { name = "x" } } },
            },
        });
        var errors = await bad.FieldErrorsAsync();
        Assert.Equal(FieldCodes.Range, errors["attendanceThreshold"]);
        Assert.Equal(FieldCodes.Range, errors["categories[0].weight"]);
        Assert.Equal(FieldCodes.Required, errors["categories[1].name"]);

        bad = await manager.PutAsync(s.SettingsUrl, new
        {
            attendanceThreshold = 80.555m,
            attendanceWeight = 0,
            categories = new object[]
            {
                new { name = "Кейс", weight = 33.333m, works = new[] { new { name = "Кейс" } } },
                new { name = " кейс ", weight = 50, works = new[] { new { name = "К" } } },
            },
        });
        errors = await bad.FieldErrorsAsync();
        Assert.Equal(FieldCodes.TooManyDecimals, errors["attendanceThreshold"]);
        Assert.Equal(FieldCodes.TooManyDecimals, errors["categories[0].weight"]);
        Assert.Equal(FieldCodes.Duplicate, errors["categories[1].name"]);
        Assert.Empty(ManualWorks(await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl)));

        var settings = await SetElementsAsync(manager, s, 80, (null, "Кейс", 100));
        var id = ManualWorks(settings)[0].Id;
        await SaveSettingsAsync(manager, s, 10);

        var res = await manager.PutAsync($"{s.Url}/rows", new
        {
            rows = new[] { Row(s.First.Id, attended: -1, manual: 11), Row(s.Second.Id, manual: -1) }
        });
        errors = await res.FieldErrorsAsync();
        Assert.Equal(FieldCodes.Range, errors["rows[0].attendedLessons"]);
        Assert.Equal(FieldCodes.Range, errors["rows[0].manualFinal"]);
        Assert.Equal(FieldCodes.Range, errors["rows[1].manualFinal"]);

        res = await manager.PutAsync($"{s.Url}/rows", new
        {
            rows = new[]
            {
                Row(s.First.Id, new() { [id] = 10.01m }, attended: 11),
                Row(s.Second.Id, new() { [id] = 7.555m }),
                Row(other.Id),
                Row(s.First.Id, new() { [Guid.NewGuid()] = 5, [id] = -0.01m }),
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        errors = await res.FieldErrorsAsync();
        Assert.Equal(FieldCodes.Range, errors[$"rows[0].scores.{id}"]);
        Assert.Equal(FieldCodes.AttendedExceedsTotal, errors["rows[0].attendedLessons"]);
        Assert.Equal(FieldCodes.TooManyDecimals, errors[$"rows[1].scores.{id}"]);
        Assert.Equal(FieldCodes.NotFound, errors["rows[2].studentId"]);
        Assert.Equal(FieldCodes.Duplicate, errors["rows[3].studentId"]);
        Assert.Equal(FieldCodes.Range, errors[$"rows[3].scores.{id}"]);
        Assert.All((await manager.GetJsonAsync<WorkSheetDto>(s.Url)).Rows, r => Assert.Empty(r.Scores));

        res = await manager.PutAsync($"{s.Url}/rows", new { rows = new[] { Row(s.First.Id, attended: 11) } });
        Assert.Equal(FieldCodes.AttendedExceedsTotal, (await res.FieldErrorsAsync())["rows[0].attendedLessons"]);

        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [id] = 10 }, attended: 10, manual: 10));
        Assert.Equal(10, RowOf(sheet, s.First.Id).Final);
        res = await manager.PutAsync($"{s.Url}/settings", new { totalLessons = 9 });
        Assert.Equal(FieldCodes.AttendedExceedsTotal, (await res.FieldErrorsAsync())["totalLessons"]);

        var noTotal = await SeedAsync(manager);
        await SetElementsAsync(manager, noTotal, 80, (null, "Кейс", 100));
        res = await manager.PutAsync($"{noTotal.Url}/rows", new { rows = new[] { Row(noTotal.First.Id, attended: 1) } });
        Assert.Equal(FieldCodes.TotalLessonsRequired, (await res.FieldErrorsAsync())["rows[0].attendedLessons"]);
    }

    [Fact]
    public async Task Manual_final_is_kept_through_recalculation_and_can_be_cleared()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Кейс", 50), (null, "Экзамен", 50));
        var (a, b) = (ManualWorks(settings)[0].Id, ManualWorks(settings)[1].Id);

        var row = RowOf(await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 7, [b] = 7 }, manual: 9)), s.First.Id);
        Assert.Equal(7, row.CalculatedRounded);
        Assert.Equal(9, row.ManualFinal);
        Assert.Equal(9, row.Final);

        row = RowOf(await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 5, [b] = 5 }, manual: 9)), s.First.Id);
        Assert.Equal(5, row.CalculatedRounded);
        Assert.Equal(9, row.Final);

        await SetElementsAsync(manager, s, null, (a, "Кейс", 20), (b, "Экзамен", 80));
        row = RowOf(await manager.GetJsonAsync<WorkSheetDto>(s.Url), s.First.Id);
        Assert.Equal(9, row.ManualFinal);

        // The override does not replace the scores: with a score missing the row is not ready.
        row = RowOf(await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 5 }, manual: 9)), s.First.Id);
        Assert.Equal(CalculationState.MissingScores, row.CalculationState);
        Assert.Equal(9, row.Final);
        await SaveSettingsAsync(manager, s, null);
        var preview = await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer");
        Assert.Contains(preview.Skipped, x => x.StudentId == s.First.Id && x.Reason == TransferSkipReason.MissingScores);

        row = RowOf(await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 5, [b] = 6 })), s.First.Id);
        Assert.Null(row.ManualFinal);
        Assert.Equal(5.8m, row.CalculatedRaw);
        Assert.Equal(6, row.Final);
    }

    [Fact]
    public async Task Removing_an_element_with_scores_needs_confirmation()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Кейс", 40), (null, "Экзамен", 60));
        var (a, b) = (ManualWorks(settings)[0].Id, ManualWorks(settings)[1].Id);
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 8, [b] = 9 }), Row(s.Second.Id, new() { [b] = 4 }));

        var current = await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl);
        Assert.Equal(1, ManualWorks(current).Single(e => e.Id == a).ScoreCount);
        Assert.Equal(2, ManualWorks(current).Single(e => e.Id == b).ScoreCount);
        var examCat = current.Categories.Single(c => c.Works.Any(w => w.Id == b));
        var keepExam = new
        {
            attendanceWeight = 0m,
            categories = new[]
            {
                new { id = (Guid?)examCat.Id, name = "Экзамен", weight = 100m, works = new[] { new { id = (Guid?)b, name = "Экзамен" } } },
            },
        };

        var res = await manager.PutAsync(s.SettingsUrl, keepExam);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var error = await res.ErrorJsonAsync();
        Assert.Equal(ErrorCodes.ScoresRemovalNotConfirmed, error.GetProperty("code").GetString());
        var details = error.GetProperty("details");
        Assert.Equal(1, details.GetProperty("scoreCount").GetInt32());
        var removedWork = Assert.Single(details.GetProperty("works").EnumerateArray());
        Assert.Equal(a, removedWork.GetProperty("id").GetGuid());
        Assert.Equal("Кейс", removedWork.GetProperty("name").GetString());
        Assert.Equal(1, removedWork.GetProperty("scoreCount").GetInt32());
        Assert.Equal(2, ManualWorks(await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl)).Count);

        var after = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl,
            new { keepExam.attendanceWeight, keepExam.categories, confirmScoreRemoval = true });
        Assert.Equal(b, Assert.Single(ManualWorks(after)).Id);
        var sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(9, RowOf(sheet, s.First.Id).Final);
        Assert.False(RowOf(sheet, s.First.Id).Scores.ContainsKey(a));
        Assert.Equal(4, RowOf(sheet, s.Second.Id).Final);

        // Removing an element nobody scored needs no confirmation.
        var added = await SetElementsAsync(manager, s, null, (b, "Экзамен", 50), (null, "Эссе", 50));
        examCat = added.Categories.Single(c => c.Works.Any(w => w.Id == b));
        await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            categories = new[]
            {
                new { id = (Guid?)examCat.Id, name = "Экзамен", weight = 100m, works = new[] { new { id = (Guid?)b, name = "Экзамен" } } },
            },
        });
        Assert.DoesNotContain(ManualWorks(added)[1].Id, ManualWorks(await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl)).Select(e => e.Id));
    }

    [Fact]
    public async Task Removing_a_category_with_scores_needs_confirmation()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var created = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            categories = new[]
            {
                new
                {
                    id = (Guid?)null, name = "Семинары", weight = 100m,
                    works = new[] { new { id = (Guid?)null, name = "Семинар1" }, new { id = (Guid?)null, name = "Семинар2" } },
                },
            },
        });
        var cat = created.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual);
        var w1 = cat.Works[0].Id;
        var w2 = cat.Works[1].Id;
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [w1] = 8, [w2] = 9 }));

        var empty = new { attendanceWeight = 0m, categories = Array.Empty<object>() };
        var res = await manager.PutAsync(s.SettingsUrl, empty);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var details = (await res.ErrorJsonAsync()).GetProperty("details");
        Assert.Equal(2, details.GetProperty("scoreCount").GetInt32());
        Assert.Equal("Семинары", Assert.Single(details.GetProperty("categories").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(2, details.GetProperty("works").GetArrayLength());
        Assert.Equal(2, ManualWorks(await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl)).Count);

        var after = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl,
            new { empty.attendanceWeight, empty.categories, confirmScoreRemoval = true });
        Assert.Empty(ManualWorks(after));
        var row = RowOf(await manager.GetJsonAsync<WorkSheetDto>(s.Url), s.First.Id);
        Assert.False(row.Scores.ContainsKey(w1));
        Assert.False(row.Scores.ContainsKey(w2));
        Assert.Null(row.Grade);
    }

    [Fact]
    public async Task Attendance_status_against_the_discipline_threshold()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var third = await CreateStudentAsync(manager, s.Group.Id, "Васильев");
        var fourth = await CreateStudentAsync(manager, s.Group.Id, "Григорьев");

        await SetElementsAsync(manager, s, null, (null, "Кейс", 100));
        await SaveSettingsAsync(manager, s, 10);
        var sheet = await SaveRowsAsync(manager, s,
            Row(s.First.Id, attended: 7), Row(s.Second.Id, attended: 8), Row(third.Id, attended: 9));
        Assert.All(sheet.Rows, r => Assert.Equal(AttendanceStatus.Unknown, r.AttendanceStatus));

        await SetElementsAsync(manager, s, 80, (null, "Кейс", 100));
        sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(80, sheet.AttendanceThreshold);
        Assert.Equal((70m, AttendanceStatus.NotMet), (RowOf(sheet, s.First.Id).AttendancePercent!.Value, RowOf(sheet, s.First.Id).AttendanceStatus));
        Assert.Equal((80m, AttendanceStatus.Met), (RowOf(sheet, s.Second.Id).AttendancePercent!.Value, RowOf(sheet, s.Second.Id).AttendanceStatus));
        Assert.Equal((90m, AttendanceStatus.Met), (RowOf(sheet, third.Id).AttendancePercent!.Value, RowOf(sheet, third.Id).AttendanceStatus));
        Assert.Null(RowOf(sheet, fourth.Id).AttendedLessons);
        Assert.Equal(AttendanceStatus.Unknown, RowOf(sheet, fourth.Id).AttendanceStatus);

        // Attendance never blocks the calculation or the transfer.
        Assert.Equal(CalculationState.MissingScores, RowOf(sheet, s.First.Id).CalculationState);

        var zero = await SeedAsync(manager);
        await SetElementsAsync(manager, zero, 80, (null, "Кейс", 100));
        await SaveSettingsAsync(manager, zero, 0);
        sheet = await SaveRowsAsync(manager, zero, Row(zero.First.Id, attended: 0));
        Assert.Equal(AttendanceStatus.Unknown, RowOf(sheet, zero.First.Id).AttendanceStatus);
        Assert.Null(RowOf(sheet, zero.First.Id).AttendancePercent);
    }

    [Fact]
    public async Task Transfer_writes_only_the_final_grade_once_and_confirms_changes()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, 80, (null, "Кейс", 50), (null, "Экзамен", 50));
        var (a, b) = (ManualWorks(settings)[0].Id, ManualWorks(settings)[1].Id);
        await SaveSettingsAsync(manager, s, 10);
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 7, [b] = 7.4m }, attended: 3), Row(s.Second.Id, new() { [a] = 9 }));

        var preview = await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer");
        Assert.Equal(s.Period.Id, preview.PeriodId);
        var item = Assert.Single(preview.Items);
        Assert.Equal((s.First.Id, 7, 70, TransferChange.New, (int?)null), (item.StudentId, item.Final, item.Value, item.Change, item.CurrentValue));
        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal((s.Second.Id, TransferSkipReason.MissingScores, 1), (skipped.StudentId, skipped.Reason, skipped.MissingCount));

        // Saving the sheet published nothing; the gradebook is untouched until the transfer.
        Assert.Empty(await GradesOf(manager, s));

        var result = await manager.PostJsonAsync<TransferResultDto>($"{s.Url}/transfer",
            new { entries = new[] { new { studentId = s.First.Id, value = 70 } } });
        Assert.Equal(new TransferResultDto(1, 0, 0), result);
        var grade = Assert.Single(await GradesOf(manager, s));
        Assert.Equal((s.First.Id, 70, GradeStatus.Draft), (grade.StudentId, grade.Value, grade.Status));
        Assert.Single(await HistoryOf(manager, grade.Id));
        var log = (await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
            "/api/manager/change-log?entityType=Grade&page=1&pageSize=100")).Items.Single(i => i.EntityId == grade.Id);
        Assert.Equal("workSheet", log.Source);

        // Repeat: no duplicate grade and no history entry.
        result = await manager.PostJsonAsync<TransferResultDto>($"{s.Url}/transfer",
            new { entries = new[] { new { studentId = s.First.Id, value = 70 } } });
        Assert.Equal(new TransferResultDto(0, 0, 1), result);
        Assert.Single(await GradesOf(manager, s));
        Assert.Single(await HistoryOf(manager, grade.Id));

        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 8, [b] = 8 }, attended: 3));
        Assert.Equal(7, RowOf(sheet, s.First.Id).TransferredFinal);
        Assert.Equal(70, RowOf(sheet, s.First.Id).Grade!.Value);
        preview = await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer");
        item = Assert.Single(preview.Items);
        Assert.Equal((TransferChange.Changed, 70, 80), (item.Change, item.CurrentValue!.Value, item.Value));

        var stale = await manager.PostAsync($"{s.Url}/transfer", new { entries = new[] { new { studentId = s.First.Id, value = 70 } } });
        Assert.Equal(ErrorCodes.Conflict, await stale.ErrorCodeAsync());
        var unconfirmed = await manager.PostAsync($"{s.Url}/transfer", new { entries = new[] { new { studentId = s.First.Id, value = 80 } } });
        Assert.Equal(ErrorCodes.GradeChangesNotConfirmed, await unconfirmed.ErrorCodeAsync());
        Assert.Equal(70, Assert.Single(await GradesOf(manager, s)).Value);

        result = await manager.PostJsonAsync<TransferResultDto>($"{s.Url}/transfer",
            new { entries = new[] { new { studentId = s.First.Id, value = 80 } }, confirmChanges = true });
        Assert.Equal(new TransferResultDto(0, 1, 0), result);
        grade = Assert.Single(await GradesOf(manager, s));
        Assert.Equal(80, grade.Value);
        var history = await HistoryOf(manager, grade.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal((70, 80), (history[0].OldValue!.Value, history[0].NewValue!.Value));

        // Weight changes recalculate the sheet but never rewrite the published grade.
        await manager.PostJsonAsync<GradeDto>($"/api/manager/grades/{grade.Id}/publish");
        await SetElementsAsync(manager, s, 80, (a, "Кейс", 10), (b, "Экзамен", 90));
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 10, [b] = 2 }, attended: 3));
        sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(3, RowOf(sheet, s.First.Id).Final);
        Assert.Equal(8, RowOf(sheet, s.First.Id).TransferredFinal);
        grade = Assert.Single(await GradesOf(manager, s));
        Assert.Equal((80, GradeStatus.Published), (grade.Value, grade.Status));
        Assert.Equal(3, (await HistoryOf(manager, grade.Id)).Count);

        // Only the final value reached the student: no scores or attendance in the student view.
        var studentGrades = await (await StudentAsync(s.First)).GetJsonAsync<JsonElement>("/api/student/grades");
        var json = studentGrades.GetRawText();
        Assert.Contains("80", json);
        Assert.DoesNotContain("attend", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sheets_of_different_periods_keep_independent_results()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var spring = await CreatePeriodAsync(manager);
        var settings = await SetElementsAsync(manager, s, 80, (null, "Кейс", 50), (null, "Экзамен", 50));
        var (a, b) = (ManualWorks(settings)[0].Id, ManualWorks(settings)[1].Id);

        await SaveSettingsAsync(manager, s, 10);
        var autumn = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [a] = 7, [b] = 7 }, attended: 9, manual: 8));
        await TransferAsync(manager, s.Url, false, (s.First.Id, 80));

        // The other period starts as an empty sheet with the shared discipline elements.
        var springUrl = s.UrlFor(spring.Id);
        var empty = await manager.GetJsonAsync<WorkSheetDto>(springUrl);
        Assert.Null(empty.Id);
        Assert.Equal((spring.Id, spring.Name), (empty.PeriodId, empty.PeriodName));
        Assert.Equal(new[] { a, b }, WorkIds(empty));
        Assert.Null(empty.TotalLessons);
        Assert.False(empty.HasTransfers);
        var emptyRow = RowOf(empty, s.First.Id);
        Assert.Empty(emptyRow.Scores);
        Assert.Null(emptyRow.AttendedLessons);
        Assert.Null(emptyRow.ManualFinal);
        Assert.Null(emptyRow.TransferredFinal);
        Assert.Null(emptyRow.Grade);

        await SaveSettingsAsync(manager, springUrl, 20);
        var second = await SaveRowsAsync(manager, springUrl, Row(s.First.Id, new() { [a] = 4, [b] = 6 }, attended: 15));
        Assert.NotEqual(autumn.Id, second.Id);
        Assert.Equal(5, RowOf(second, s.First.Id).Final);

        var old = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal((autumn.Id, s.Period.Id, (int?)10, true), (old.Id, old.PeriodId, old.TotalLessons, old.HasTransfers));
        var oldRow = RowOf(old, s.First.Id);
        Assert.Equal(7m, oldRow.Scores[a]);
        Assert.Equal(7m, oldRow.Scores[b]);
        Assert.Equal((9, 8, 8, 80), (oldRow.AttendedLessons!.Value, oldRow.ManualFinal!.Value, oldRow.TransferredFinal!.Value,
            oldRow.Grade!.Value));
        Assert.NotNull(oldRow.TransferredAt);

        var list = await manager.GetJsonAsync<List<WorkSheetSummaryDto>>(
            $"/api/manager/worksheets?groupId={s.Group.Id}&disciplineId={s.Discipline.Id}");
        Assert.Equal(2, list.Count);
        Assert.True(list.Single(x => x.PeriodId == s.Period.Id).HasTransfers);
        Assert.False(list.Single(x => x.PeriodId == spring.Id).HasTransfers);
    }

    [Fact]
    public async Task Period_of_a_transferred_sheet_cannot_be_changed()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var other = await CreatePeriodAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Кейс", 100));
        await SaveSettingsAsync(manager, s, 10);
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [ManualWorks(settings)[0].Id] = 9 }));
        await TransferAsync(manager, s.Url, false, (s.First.Id, 90));

        // The period is the sheet's identity: a period in the settings body is ignored, even with a confirmation.
        var after = await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/settings",
            new { periodId = other.Id, totalLessons = 12, confirmPeriodChange = true });
        Assert.Equal((sheet.Id, s.Period.Id, (int?)12), (after.Id, after.PeriodId, after.TotalLessons));
        Assert.Equal((9, 90), (RowOf(after, s.First.Id).TransferredFinal!.Value, RowOf(after, s.First.Id).Grade!.Value));

        var otherSheet = await manager.GetJsonAsync<WorkSheetDto>(s.UrlFor(other.Id));
        Assert.Null(otherSheet.Id);
        Assert.All(otherSheet.Rows, r => Assert.Null(r.Grade));
        var grade = Assert.Single(await GradesOf(manager, s));
        Assert.Equal(s.Period.Id, grade.PeriodId);

        var list = await manager.GetJsonAsync<List<WorkSheetSummaryDto>>(
            $"/api/manager/worksheets?groupId={s.Group.Id}&disciplineId={s.Discipline.Id}");
        Assert.Equal(s.Period.Id, Assert.Single(list).PeriodId);
    }

    [Fact]
    public async Task Repeat_transfer_updates_grades_of_its_own_period_only()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var spring = await CreatePeriodAsync(manager);
        var springUrl = s.UrlFor(spring.Id);
        var id = ManualWorks(await SetElementsAsync(manager, s, null, (null, "Кейс", 100)))[0].Id;

        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [id] = 6 }));
        await SaveRowsAsync(manager, springUrl, Row(s.First.Id, new() { [id] = 9 }));
        Assert.Equal(new TransferResultDto(1, 0, 0), await TransferAsync(manager, s.Url, false, (s.First.Id, 60)));
        Assert.Equal(new TransferResultDto(1, 0, 0), await TransferAsync(manager, springUrl, false, (s.First.Id, 90)));

        var preview = await manager.GetJsonAsync<TransferPreviewDto>($"{springUrl}/transfer");
        Assert.Equal((spring.Id, spring.Name), (preview.PeriodId, preview.PeriodName));
        Assert.Equal((TransferChange.Unchanged, (int?)90), (preview.Items.Single().Change, preview.Items.Single().CurrentValue));

        await SaveRowsAsync(manager, springUrl, Row(s.First.Id, new() { [id] = 10 }));
        Assert.Equal(new TransferResultDto(0, 1, 0), await TransferAsync(manager, springUrl, true, (s.First.Id, 100)));

        var grades = await GradesOf(manager, s);
        Assert.Equal(2, grades.Count);
        var autumnGrade = grades.Single(g => g.PeriodId == s.Period.Id);
        var springGrade = grades.Single(g => g.PeriodId == spring.Id);
        Assert.Equal((60, 100), (autumnGrade.Value, springGrade.Value));
        Assert.Single(await HistoryOf(manager, autumnGrade.Id));
        Assert.Equal(2, (await HistoryOf(manager, springGrade.Id)).Count);

        var autumn = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal((6, 60), (RowOf(autumn, s.First.Id).TransferredFinal!.Value, RowOf(autumn, s.First.Id).Grade!.Value));
        Assert.Equal(TransferChange.Unchanged,
            (await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer")).Items.Single().Change);
    }

    [Fact]
    public async Task Transfer_requires_complete_weights()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Кейс", 100));
        await SaveSettingsAsync(manager, s, null);
        var caseId = ManualWorks(settings)[0].Id;
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [caseId] = 6 }));
        await SetElementsAsync(manager, s, null, (caseId, "Кейс", 90));

        var preview = await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer");
        Assert.Empty(preview.Items);
        Assert.All(preview.Skipped, x => Assert.Equal(TransferSkipReason.WeightsIncomplete, x.Reason));
        var res = await manager.PostAsync($"{s.Url}/transfer", new { entries = new[] { new { studentId = s.First.Id, value = 60 } } });
        Assert.Equal(ErrorCodes.WorkSheetWeightsIncomplete, await res.ErrorCodeAsync());
        Assert.Empty(await GradesOf(manager, s));
    }

    [Fact]
    public async Task Student_who_left_the_group_keeps_a_read_only_row_and_starts_empty_in_the_new_group()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Кейс", 100));
        await SaveSettingsAsync(manager, s, 10);
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [ManualWorks(settings)[0].Id] = 8 }, attended: 9));

        var newGroup = await CreateGroupAsync(manager);
        await manager.PutJsonAsync<StudentDto>($"/api/manager/students/{s.First.Id}", new
        {
            lastName = "Алексеев", firstName = "Иван", email = s.First.Email, groupId = newGroup.Id
        });

        var old = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        var moved = RowOf(old, s.First.Id);
        Assert.False(moved.InGroup);
        Assert.Equal(newGroup.Name, moved.CurrentGroupName);
        Assert.Equal(8, moved.Final);
        Assert.Equal(9, moved.AttendedLessons);
        Assert.Equal(s.First.Id, old.Rows[^1].StudentId);

        var res = await manager.PutAsync($"{s.Url}/rows", new { rows = new[] { Row(s.First.Id) } });
        Assert.Equal(FieldCodes.NotFound, (await res.FieldErrorsAsync())["rows[0].studentId"]);
        var preview = await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer");
        Assert.Contains(preview.Skipped, x => x.StudentId == s.First.Id && x.Reason == TransferSkipReason.NotInGroup);
        Assert.DoesNotContain(preview.Items, x => x.StudentId == s.First.Id);

        var fresh = await manager.GetJsonAsync<WorkSheetDto>($"/api/manager/worksheets/{newGroup.Id}/{s.Discipline.Id}/{s.Period.Id}");
        var row = RowOf(fresh, s.First.Id);
        Assert.True(row.InGroup);
        Assert.Empty(row.Scores);
        Assert.Null(row.AttendedLessons);

        var delete = await manager.DeleteAsync($"/api/manager/students/{s.First.Id}");
        Assert.Equal(ErrorCodes.InUse, await delete.ErrorCodeAsync());
        Assert.Equal(ErrorCodes.InUse, await (await manager.DeleteAsync($"/api/manager/disciplines/{s.Discipline.Id}")).ErrorCodeAsync());
    }

    [Fact]
    public async Task Students_cannot_reach_the_working_sheet()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        await SetElementsAsync(manager, s, 80, (null, "Кейс", 100));
        var student = await StudentAsync(s.First);

        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(s.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(s.SettingsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"{s.Url}/transfer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.PutAsync($"{s.Url}/rows", new { rows = new[] { Row(s.First.Id, manual: 10) } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.PostAsync($"{s.Url}/transfer", new { entries = Array.Empty<object>() })).StatusCode);
        Assert.Null(RowOf(await manager.GetJsonAsync<WorkSheetDto>(s.Url), s.First.Id).ManualFinal);
    }

    [Fact]
    public async Task Row_changes_are_logged_per_student_without_scores()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Кейс", 100));
        var id = ManualWorks(settings)[0].Id;
        await SaveSettingsAsync(manager, s, 10);
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [id] = 7.25m }, attended: 5, manual: 9), Row(s.Second.Id));
        await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [id] = 6 }, attended: 5));

        var entries = (await manager.GetJsonAsync<PagedResult<ChangeLogListItemDto>>(
                "/api/manager/change-log?entityType=WorkSheet&page=1&pageSize=100")).Items
            .Where(i => i.EntityId == sheet.Id).OrderBy(i => i.OccurredAt).ThenBy(i => i.Id).ToList();
        // Settings, the first student's row twice; the untouched second student produces nothing.
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.True(e.EntityExists));
        var created = entries[1];
        Assert.Equal(ChangeLogAction.Created, created.Action);
        Assert.Contains(s.Discipline.Name, created.EntityTitle);
        Assert.Equal([s.Group.Name], created.GroupNames);
        Assert.Equal("set", created.Changes.Single(c => c.Field == "score:Кейс").New?.GetString());
        Assert.Equal("set", created.Changes.Single(c => c.Field == "manualFinal").New?.GetString());
        Assert.Equal(5, created.Changes.Single(c => c.Field == "attendedLessons").New?.GetInt32());
        var updated = entries[2];
        Assert.Equal("changed", updated.Changes.Single(c => c.Field == "score:Кейс").New?.GetString());
        Assert.Equal("cleared", updated.Changes.Single(c => c.Field == "manualFinal").New?.GetString());
        Assert.DoesNotContain(updated.Changes, c => c.Field == "attendedLessons");
        foreach (var c in entries.SelectMany(e => e.Changes).Where(c => c.Field != "attendedLessons" && c.Field != "totalLessons"))
        {
            Assert.NotEqual(JsonValueKind.Number, c.Old?.ValueKind ?? JsonValueKind.Null);
            Assert.NotEqual(JsonValueKind.Number, c.New?.ValueKind ?? JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Category_average_attendance_formula_and_zero_weight_do_not_block()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var created = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceThreshold = 80m,
            attendanceWeight = 20m,
            categories = new[]
            {
                new { name = "Лекции", weight = 20m, works = new[] { new { name = "Лекция 1" }, new { name = "Лекция 2" }, new { name = "Лекция 3" } } },
                new { name = "Семинары", weight = 30m, works = new[] { new { name = "Семинар 1" }, new { name = "Семинар 2" } } },
                new { name = "Контрольные", weight = 30m, works = new[] { new { name = "КР 1" } } },
            },
        });
        Assert.True(created.WeightsComplete);
        Assert.Equal(20, created.AttendanceWeight);
        var lectures = created.Categories.Single(c => c.Name == "Лекции");
        var seminars = created.Categories.Single(c => c.Name == "Семинары");
        var exam = created.Categories.Single(c => c.Name == "Контрольные");
        var (l1, l2, l3) = (lectures.Works[0].Id, lectures.Works[1].Id, lectures.Works[2].Id);
        var (s1, s2) = (seminars.Works[0].Id, seminars.Works[1].Id);
        var kr = exam.Works[0].Id;

        await SaveSettingsAsync(manager, s, 10);
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new()
        {
            [l1] = 8, [l2] = 6, [l3] = 10, [s1] = 7, [s2] = 9, [kr] = 9,
        }, attended: 6));
        var first = RowOf(sheet, s.First.Id);
        Assert.Equal(8m, first.CategoryScores[lectures.Id]);
        Assert.Equal(6m, first.AttendanceScore);
        Assert.Equal(1.2m, first.AttendanceContribution);
        Assert.Equal(7.9m, first.CalculatedRaw);
        Assert.Equal(8, first.Final);
        Assert.Equal(AttendanceStatus.NotMet, first.AttendanceStatus);

        var reopened = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(7.9m, RowOf(reopened, s.First.Id).CalculatedRaw);
        Assert.Equal(6m, RowOf(reopened, s.First.Id).AttendanceScore);

        var preview = await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer");
        Assert.Contains(preview.Items, x => x.StudentId == s.First.Id && x.Final == 8);

        sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new()
        {
            [l1] = 8, [l2] = 6, [s1] = 7, [s2] = 9, [kr] = 9,
        }, attended: 6));
        Assert.Equal(CalculationState.MissingScores, RowOf(sheet, s.First.Id).CalculationState);
        Assert.Contains((await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer")).Skipped,
            x => x.StudentId == s.First.Id && x.Reason == TransferSkipReason.MissingScores);

        var zero = await SeedAsync(manager);
        var settings = await manager.PutJsonAsync<AssessmentSettingsDto>(zero.SettingsUrl, new
        {
            attendanceWeight = 0m,
            categories = new[]
            {
                new { name = "Экзамен", weight = 100m, works = new[] { new { name = "Экзамен" } } },
                new { name = "Черновик", weight = 0m, works = new[] { new { name = "Необязательная" } } },
            },
        });
        var examId = settings.Categories.Single(c => c.Name == "Экзамен").Works[0].Id;
        sheet = await SaveRowsAsync(manager, zero, Row(zero.First.Id, new() { [examId] = 8 }));
        Assert.Equal(CalculationState.Calculated, RowOf(sheet, zero.First.Id).CalculationState);
        Assert.Equal(8, RowOf(sheet, zero.First.Id).Final);
        Assert.Contains((await manager.GetJsonAsync<TransferPreviewDto>($"{zero.Url}/transfer")).Items,
            x => x.StudentId == zero.First.Id);
    }

    [Fact]
    public async Task Manual_attendance_category_stays_distinct_from_automatic_attendance()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await SetElementsAsync(manager, s, null, (null, "Посещаемость", 100));
        var manual = settings.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual);
        var auto = settings.Categories.Single(c => c.Kind == AssessmentCategoryKind.Attendance);
        Assert.True(manual.LegacyManualAttendance);
        Assert.False(auto.LegacyManualAttendance);
        Assert.Equal(0, auto.Weight);
        Assert.Equal(100, manual.Weight);
        var workId = Assert.Single(manual.Works).Id;

        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [workId] = 7 }));
        Assert.Equal(7, RowOf(sheet, s.First.Id).Final);
        Assert.Null(RowOf(sheet, s.First.Id).AttendanceScore);
        Assert.Equal(workId, Assert.Single(RowOf(sheet, s.First.Id).Scores).Key);
    }

    [Theory]
    [InlineData("посещаемость")]
    [InlineData("Посещаемость")]
    [InlineData("ПОСЕЩАЕМОСТЬ")]
    public async Task Creating_a_manual_category_named_like_attendance_does_not_change_its_kind(string name)
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            categories = new[] { new { name, weight = 100m, works = new[] { new { name = "Работа" } } } },
        });
        var manual = Assert.Single(settings.Categories, c => c.Kind == AssessmentCategoryKind.Manual);
        var auto = Assert.Single(settings.Categories, c => c.Kind == AssessmentCategoryKind.Attendance);
        Assert.Equal(name, manual.Name);
        Assert.Equal(AssessmentCategoryKind.Manual, manual.Kind);
        Assert.Equal(AssessmentCategoryKind.Attendance, auto.Kind);
        Assert.Equal(0, auto.Weight);
    }

    [Fact]
    public async Task Renaming_a_manual_category_does_not_change_kind_formula_or_scores()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var created = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 20m,
            categories = new[] { new { name = "Лекции", weight = 80m, works = new[] { new { name = "Лекция 1" } } } },
        });
        var lectures = created.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual);
        var workId = Assert.Single(lectures.Works).Id;
        await SaveSettingsAsync(manager, s, 10);
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [workId] = 8 }, attended: 6));
        Assert.Equal(7.6m, RowOf(sheet, s.First.Id).CalculatedRaw);
        Assert.Equal(6m, RowOf(sheet, s.First.Id).AttendanceScore);
        Assert.Equal(8m, RowOf(sheet, s.First.Id).Scores[workId]);

        var renamed = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 20m,
            categories = new[]
            {
                new { id = lectures.Id, name = "ПОСЕЩАЕМОСТЬ", weight = 80m, works = new[] { new { id = workId, name = "Лекция 1" } } },
            },
        });
        var manual = Assert.Single(renamed.Categories, c => c.Kind == AssessmentCategoryKind.Manual);
        Assert.Equal(lectures.Id, manual.Id);
        Assert.Equal("ПОСЕЩАЕМОСТЬ", manual.Name);
        Assert.Equal(AssessmentCategoryKind.Manual, manual.Kind);
        Assert.Equal(workId, Assert.Single(manual.Works).Id);
        Assert.Equal(AssessmentCategoryKind.Attendance, Assert.Single(renamed.Categories, c => c.Kind == AssessmentCategoryKind.Attendance).Kind);

        var reopened = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        var row = RowOf(reopened, s.First.Id);
        Assert.Equal(8m, row.Scores[workId]);
        Assert.Equal(7.6m, row.CalculatedRaw);
        Assert.Equal(6m, row.AttendanceScore);
        Assert.Equal(8, row.Final);
    }

    [Fact]
    public async Task Weighted_attendance_without_total_is_not_zero_and_skips_transfer()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var settings = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 20m,
            categories = new[] { new { name = "Экзамен", weight = 80m, works = new[] { new { name = "Экзамен" } } } },
        });
        var examId = settings.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual).Works[0].Id;
        var sheet = await SaveRowsAsync(manager, s, Row(s.First.Id, new() { [examId] = 10 }, attended: null));
        Assert.Equal(CalculationState.MissingScores, RowOf(sheet, s.First.Id).CalculationState);
        Assert.Null(RowOf(sheet, s.First.Id).AttendanceScore);
        Assert.Contains((await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer")).Skipped,
            x => x.StudentId == s.First.Id);
        Assert.Empty((await manager.GetJsonAsync<TransferPreviewDto>($"{s.Url}/transfer")).Items);
    }

    private static async Task<List<GradeDto>> GradesOf(ApiClient manager, Seed s) =>
        await manager.GetJsonAsync<List<GradeDto>>($"/api/manager/grades?groupId={s.Group.Id}&disciplineId={s.Discipline.Id}");

    private static Task<List<GradeHistoryDto>> HistoryOf(ApiClient manager, Guid gradeId) =>
        manager.GetJsonAsync<List<GradeHistoryDto>>($"/api/manager/grades/{gradeId}/history");
}
