using System.Net;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Tests.Infrastructure;

namespace MbaLms.Api.Tests;

public class CalculationModeTests(ApiFactory factory) : TestBase(factory)
{
    private sealed record Seed(GroupDto Group, StudentAccount First, StudentAccount Second, DisciplineDto Discipline,
        PeriodDto Period)
    {
        public string Url => $"/api/manager/worksheets/{Group.Id}/{Discipline.Id}/{Period.Id}";
        public string SettingsUrl => $"/api/manager/worksheets/disciplines/{Discipline.Id}/settings";
    }

    private async Task<Seed> SeedAsync(ApiClient manager)
    {
        var group = await CreateGroupAsync(manager);
        var first = await CreateStudentAsync(manager, group.Id, "Алексеев");
        var second = await CreateStudentAsync(manager, group.Id, "Борисов");
        return new Seed(group, first, second, await CreateDisciplineAsync(manager), await CreatePeriodAsync(manager));
    }

    private static WorkSheetRowDto RowOf(WorkSheetDto sheet, Guid studentId) =>
        sheet.Rows.Single(r => r.StudentId == studentId);

    [Fact]
    public async Task New_and_existing_disciplines_default_to_weighted_with_works_included()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var empty = await manager.GetJsonAsync<AssessmentSettingsDto>(s.SettingsUrl);
        Assert.Equal(CalculationMode.Weighted, empty.CalculationMode);
        Assert.False(empty.IncludeAttendanceInAverage);
        Assert.False(empty.ReadyForTransfer);

        var saved = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            categories = new[] { new { name = "Экзамен", weight = 100m, works = new[] { new { name = "Экзамен" } } } },
        });
        Assert.Equal(CalculationMode.Weighted, saved.CalculationMode);
        Assert.True(saved.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual).IncludeInAverage);
        Assert.True(saved.ReadyForTransfer);
        var sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(CalculationMode.Weighted, sheet.CalculationMode);
        Assert.True(sheet.ReadyForTransfer);
    }

    [Fact]
    public async Task Average_means_included_works_ignores_weight_and_does_not_touch_grades()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var weighted = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            categories = new[]
            {
                new { name = "Лекции", weight = 20m, works = new[] { new { name = "L1" }, new { name = "L2" } } },
                new { name = "Черновик", weight = 0m, works = new[] { new { name = "Draft" } } },
                new { name = "Экзамен", weight = 80m, works = new[] { new { name = "KR" } } },
            },
        });
        var lectures = weighted.Categories.Single(c => c.Name == "Лекции");
        var draft = weighted.Categories.Single(c => c.Name == "Черновик");
        var exam = weighted.Categories.Single(c => c.Name == "Экзамен");
        var (l1, l2, unused, kr) = (lectures.Works[0].Id, lectures.Works[1].Id, draft.Works[0].Id, exam.Works[0].Id);

        await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/settings", new { totalLessons = 10 });
        var filled = await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/rows", new
        {
            rows = new[]
            {
                new
                {
                    studentId = s.First.Id,
                    scores = new Dictionary<Guid, decimal?> { [l1] = 8, [l2] = 6, [unused] = 10, [kr] = 9 },
                    attendedLessons = 6, manualFinal = 5,
                },
            },
        });
        Assert.Equal(8.6m, RowOf(filled, s.First.Id).CalculatedRaw);
        Assert.Equal(5, RowOf(filled, s.First.Id).Final);

        var transferred = await manager.PostJsonAsync<TransferResultDto>($"{s.Url}/transfer",
            new { entries = new[] { new { studentId = s.First.Id, value = 50 } } });
        Assert.Equal(1, transferred.Created);
        var grade = Assert.Single(await manager.GetJsonAsync<List<GradeDto>>(
            $"/api/manager/grades?groupId={s.Group.Id}&disciplineId={s.Discipline.Id}"));
        Assert.Equal(50, grade.Value);

        var average = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            calculationMode = CalculationMode.Average,
            includeAttendanceInAverage = false,
            categories = new[]
            {
                new { id = lectures.Id, name = "Лекции", weight = 20m, includeInAverage = true,
                    works = new[] { new { id = l1, name = "L1" }, new { id = l2, name = "L2" } } },
                new { id = draft.Id, name = "Черновик", weight = 0m, includeInAverage = true,
                    works = new[] { new { id = unused, name = "Draft" } } },
                new { id = exam.Id, name = "Экзамен", weight = 80m, includeInAverage = true,
                    works = new[] { new { id = kr, name = "KR" } } },
            },
        });
        Assert.Equal(CalculationMode.Average, average.CalculationMode);
        Assert.Equal(["L1", "L2", "Draft", "KR"], average.AverageTerms);
        Assert.True(average.ReadyForTransfer);
        Assert.True(average.WeightsComplete);

        var sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        var row = RowOf(sheet, s.First.Id);
        Assert.Equal(8.25m, row.CalculatedRaw);
        Assert.Equal(8, row.CalculatedRounded);
        Assert.Equal(5, row.ManualFinal);
        Assert.Equal(5, row.Final);
        Assert.Equal(8m, row.Scores[l1]);
        Assert.Equal(10m, row.Scores[unused]);
        Assert.Equal(5, row.TransferredFinal);
        Assert.Equal(50, row.Grade!.Value);
        Assert.Equal(50, (await manager.GetJsonAsync<List<GradeDto>>(
            $"/api/manager/grades?groupId={s.Group.Id}&disciplineId={s.Discipline.Id}")).Single().Value);
    }

    [Fact]
    public async Task Average_exclusion_and_attendance_flag_and_no_data_message()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var saved = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            calculationMode = CalculationMode.Average,
            includeAttendanceInAverage = false,
            categories = new[]
            {
                new { name = "Экзамен", weight = 100m, includeInAverage = true, works = new[] { new { name = "Экзамен" } } },
                new { name = "Посещаемость", weight = 0m, includeInAverage = false, works = new[] { new { name = "Явка" } } },
            },
        });
        var exam = saved.Categories.Single(c => c.Name == "Экзамен");
        var legacy = saved.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual && c.Name == "Посещаемость");
        Assert.False(legacy.IncludeInAverage);
        Assert.Equal(["Экзамен"], saved.AverageTerms);

        await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/settings", new { totalLessons = 10 });
        var sheet = await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/rows", new
        {
            rows = new[]
            {
                new
                {
                    studentId = s.First.Id,
                    scores = new Dictionary<Guid, decimal?> { [exam.Works[0].Id] = 9 },
                    attendedLessons = 6,
                },
            },
        });
        Assert.Equal(9m, RowOf(sheet, s.First.Id).CalculatedRaw);
        Assert.Equal(CalculationState.Calculated, RowOf(sheet, s.First.Id).CalculationState);

        var withAttendance = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            calculationMode = CalculationMode.Average,
            includeAttendanceInAverage = true,
            categories = new[]
            {
                new { id = exam.Id, name = "Экзамен", weight = 100m, includeInAverage = true,
                    works = new[] { new { id = exam.Works[0].Id, name = "Экзамен" } } },
                new { id = legacy.Id, name = "Посещаемость", weight = 0m, includeInAverage = false,
                    works = new[] { new { id = legacy.Works[0].Id, name = "Явка" } } },
            },
        });
        Assert.Equal(["Экзамен", AssessmentCategoryNames.Attendance], withAttendance.AverageTerms);
        sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(7.5m, RowOf(sheet, s.First.Id).CalculatedRaw);

        var none = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 0m,
            calculationMode = CalculationMode.Average,
            includeAttendanceInAverage = false,
            categories = new[]
            {
                new { id = exam.Id, name = "Экзамен", weight = 100m, includeInAverage = false,
                    works = new[] { new { id = exam.Works[0].Id, name = "Экзамен" } } },
                new { id = legacy.Id, name = "Посещаемость", weight = 0m, includeInAverage = false,
                    works = new[] { new { id = legacy.Works[0].Id, name = "Явка" } } },
            },
        });
        Assert.Empty(none.AverageTerms);
        Assert.False(none.ReadyForTransfer);
        sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(CalculationState.NoElements, RowOf(sheet, s.First.Id).CalculationState);
        Assert.Null(RowOf(sheet, s.First.Id).CalculatedRaw);
        Assert.Equal(9m, RowOf(sheet, s.First.Id).Scores[exam.Works[0].Id]);

        var blocked = await manager.PostAsync($"{s.Url}/transfer",
            new { entries = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(ErrorCodes.WorkSheetNoElements, await blocked.ErrorCodeAsync());
    }

    [Fact]
    public async Task Switching_back_to_weighted_restores_weight_formula_and_keeps_scores()
    {
        var manager = await ManagerAsync();
        var s = await SeedAsync(manager);
        var created = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 20m,
            calculationMode = CalculationMode.Average,
            includeAttendanceInAverage = true,
            categories = new[]
            {
                new { name = "Экзамен", weight = 80m, includeInAverage = true, works = new[] { new { name = "Экзамен" } } },
            },
        });
        var examId = created.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual).Works[0].Id;
        await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/settings", new { totalLessons = 10 });
        await manager.PutJsonAsync<WorkSheetDto>($"{s.Url}/rows", new
        {
            rows = new[]
            {
                new
                {
                    studentId = s.First.Id,
                    scores = new Dictionary<Guid, decimal?> { [examId] = 10 },
                    attendedLessons = 6,
                },
            },
        });
        var average = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(8m, RowOf(average, s.First.Id).CalculatedRaw);

        var weighted = await manager.PutJsonAsync<AssessmentSettingsDto>(s.SettingsUrl, new
        {
            attendanceWeight = 20m,
            calculationMode = CalculationMode.Weighted,
            includeAttendanceInAverage = true,
            categories = new[]
            {
                new
                {
                    id = created.Categories.Single(c => c.Kind == AssessmentCategoryKind.Manual).Id,
                    name = "Экзамен", weight = 80m, includeInAverage = true,
                    works = new[] { new { id = examId, name = "Экзамен" } },
                },
            },
        });
        Assert.Equal(CalculationMode.Weighted, weighted.CalculationMode);
        Assert.True(weighted.IncludeAttendanceInAverage);
        var sheet = await manager.GetJsonAsync<WorkSheetDto>(s.Url);
        Assert.Equal(9.2m, RowOf(sheet, s.First.Id).CalculatedRaw);
        Assert.Equal(10m, RowOf(sheet, s.First.Id).Scores[examId]);
        Assert.Equal(6, RowOf(sheet, s.First.Id).AttendedLessons);
    }
}
