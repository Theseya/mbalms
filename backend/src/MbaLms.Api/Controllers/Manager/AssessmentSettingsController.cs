using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record AssessmentWorkDto(Guid Id, string Name, int ScoreCount);

public record AssessmentCategoryDto(Guid Id, string Name, decimal Weight, AssessmentCategoryKind Kind,
    bool IncludeInAverage, bool LegacyManualAttendance, List<AssessmentWorkDto> Works);

public record AssessmentSettingsDto(Guid DisciplineId, string DisciplineName, decimal? AttendanceThreshold,
    decimal AttendanceWeight, CalculationMode CalculationMode, bool IncludeAttendanceInAverage,
    List<string> AverageTerms, List<AssessmentCategoryDto> Categories, decimal WeightTotal, bool WeightsComplete,
    bool ReadyForTransfer);

public record AssessmentWorkInput(
    Guid? Id,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string? Name);

public record AssessmentCategoryInput(
    Guid? Id,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string? Name,
    [Required(ErrorMessage = FieldCodes.Required)] [Range(typeof(decimal), "0", "100", ErrorMessage = FieldCodes.Range)]
    decimal? Weight,
    bool? IncludeInAverage,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(WorkSheetRules.MaxWorksPerCategory, ErrorMessage = FieldCodes.MaxLength)]
    List<AssessmentWorkInput>? Works);

public record AssessmentSettingsRequest(
    [Range(typeof(decimal), "0", "100", ErrorMessage = FieldCodes.Range)] decimal? AttendanceThreshold,
    [Required(ErrorMessage = FieldCodes.Required)] [Range(typeof(decimal), "0", "100", ErrorMessage = FieldCodes.Range)]
    decimal? AttendanceWeight,
    CalculationMode? CalculationMode,
    bool? IncludeAttendanceInAverage,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(WorkSheetRules.MaxCategories, ErrorMessage = FieldCodes.MaxLength)]
    List<AssessmentCategoryInput>? Categories,
    bool ConfirmScoreRemoval = false);

public record ScoreRemovalWorkDto(Guid Id, string Name, string CategoryName, int ScoreCount);

public record ScoreRemovalCategoryDto(Guid Id, string Name, int ScoreCount);

public record ScoreRemovalDetails(List<ScoreRemovalCategoryDto> Categories, List<ScoreRemovalWorkDto> Works, int ScoreCount);

/// <summary>
/// Assessment categories, works, attendance weight and the attendance threshold of a discipline.
/// Weights that do not add up to 100% are saved as a draft; working sheets then show no calculated results until they do.
/// </summary>
[Route("api/manager/worksheets/disciplines/{disciplineId:guid}/settings")]
public class AssessmentSettingsController(
    AppDbContext db, ChangeLogWriter changeLog, CurrentUser current, UserManager<AppUser> users)
    : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<AssessmentSettingsDto> Get(Guid disciplineId, CancellationToken ct)
    {
        var discipline = await Db.Disciplines.AsNoTracking().FirstOrDefaultAsync(d => d.Id == disciplineId, ct)
                         ?? throw AppException.NotFound();
        var categories = await LoadCategoriesAsync(disciplineId, asNoTracking: true, ct);
        return ToDto(discipline, categories,
            await SettingsAsync(disciplineId, ct),
            await ScoreCountsAsync(categories.SelectMany(c => c.Works.Select(w => w.Id)).ToList(), ct));
    }

    [HttpPut]
    public async Task<AssessmentSettingsDto> Save(Guid disciplineId, AssessmentSettingsRequest r, CancellationToken ct)
    {
        var discipline = await Db.Disciplines.FirstOrDefaultAsync(d => d.Id == disciplineId, ct) ?? throw AppException.NotFound();
        var existing = await LoadCategoriesAsync(disciplineId, asNoTracking: false, ct);
        var manuals = existing.Where(c => c.Kind == AssessmentCategoryKind.Manual).ToDictionary(c => c.Id);
        var worksById = existing.SelectMany(c => c.Works).ToDictionary(w => w.Id);
        Validate(r, manuals, worksById);

        var keptWorkIds = (r.Categories ?? []).SelectMany(c => c.Works ?? []).Where(w => w.Id is not null)
            .Select(w => w.Id!.Value).ToHashSet();
        var keptCategoryIds = (r.Categories ?? []).Where(c => c.Id is not null).Select(c => c.Id!.Value).ToHashSet();
        var removedWorks = existing.SelectMany(c => c.Works).Where(w => !keptWorkIds.Contains(w.Id)).ToList();
        var removedManuals = manuals.Values.Where(c => !keptCategoryIds.Contains(c.Id)).ToList();
        var removedIds = removedWorks.Select(w => w.Id).ToList();
        var removedCounts = await ScoreCountsAsync(removedIds, ct);
        var scoreCount = removedCounts.Values.Sum();
        if (scoreCount > 0 && !r.ConfirmScoreRemoval)
        {
            var categoryName = existing.ToDictionary(c => c.Id, c => c.Name);
            throw AppException.Conflict(ErrorCodes.ScoresRemovalNotConfirmed, new ScoreRemovalDetails(
                removedManuals.Select(c => new ScoreRemovalCategoryDto(c.Id, c.Name,
                    c.Works.Sum(w => removedCounts.GetValueOrDefault(w.Id)))).ToList(),
                removedWorks.Select(w => new ScoreRemovalWorkDto(w.Id, w.Name, categoryName[w.CategoryId],
                    removedCounts.GetValueOrDefault(w.Id))).ToList(),
                scoreCount));
        }

        var rows = await Db.WorkSheetRows.Include(x => x.Scores).Include(x => x.WorkSheet)
            .Where(x => x.WorkSheet!.DisciplineId == disciplineId).ToListAsync(ct);
        var actorEmail = await ActorEmailAsync();

        foreach (var work in removedWorks)
        {
            changeLog.Add(ChangeLogEntityType.AssessmentElement, work.Id, ChangeLogAction.Deleted,
                [new("name", work.Name, null),
                    new("scoresRemoved", null, removedCounts.GetValueOrDefault(work.Id))],
                WorkSheetChangeLog.ElementContext(work.Name, discipline.Name, actorEmail));
            foreach (var row in rows) row.Scores.RemoveAll(s => s.ElementId == work.Id);
            Db.AssessmentElements.Remove(work);
        }
        foreach (var category in removedManuals)
        {
            changeLog.Add(ChangeLogEntityType.AssessmentCategory, category.Id, ChangeLogAction.Deleted,
                [new("name", category.Name, null), new("weight", category.Weight, null)],
                WorkSheetChangeLog.ElementContext(category.Name, discipline.Name, actorEmail));
            Db.AssessmentCategories.Remove(category);
        }

        var orderedManuals = new List<AssessmentCategory>();
        for (var i = 0; i < (r.Categories ?? []).Count; i++)
        {
            var input = r.Categories![i];
            var name = input.Name!.Trim();
            var weight = input.Weight!.Value;
            AssessmentCategory category;
            var includeInAverage = input.IncludeInAverage ?? true;
            if (input.Id is Guid id)
            {
                category = manuals[id];
                var changes = new List<ChangeLogField>();
                if (!string.Equals(category.Name, name, StringComparison.Ordinal)) changes.Add(new("name", category.Name, name));
                if (category.Weight != weight) changes.Add(new("weight", category.Weight, weight));
                if (category.IncludeInAverage != includeInAverage)
                    changes.Add(new("includeInAverage", category.IncludeInAverage, includeInAverage));
                if (changes.Count > 0)
                    changeLog.Add(ChangeLogEntityType.AssessmentCategory, category.Id, ChangeLogAction.Updated, changes,
                        WorkSheetChangeLog.ElementContext(name, discipline.Name, actorEmail));
                category.Name = name;
                category.Weight = weight;
                category.IncludeInAverage = includeInAverage;
                category.Order = i;
            }
            else
            {
                category = new AssessmentCategory
                {
                    Id = Guid.CreateVersion7(), DisciplineId = disciplineId, Name = name, Weight = weight,
                    Kind = AssessmentCategoryKind.Manual, IncludeInAverage = includeInAverage, Order = i
                };
                Db.AssessmentCategories.Add(category);
                changeLog.Add(ChangeLogEntityType.AssessmentCategory, category.Id, ChangeLogAction.Created,
                    [new("name", null, name), new("weight", null, weight)],
                    WorkSheetChangeLog.ElementContext(name, discipline.Name, actorEmail));
            }

            var works = new List<AssessmentElement>();
            for (var w = 0; w < (input.Works ?? []).Count; w++)
            {
                var workInput = input.Works![w];
                var workName = workInput.Name!.Trim();
                if (workInput.Id is Guid workId)
                {
                    var work = worksById[workId];
                    if (!string.Equals(work.Name, workName, StringComparison.Ordinal))
                        changeLog.Add(ChangeLogEntityType.AssessmentElement, work.Id, ChangeLogAction.Updated,
                            [new("name", work.Name, workName)],
                            WorkSheetChangeLog.ElementContext(workName, discipline.Name, actorEmail));
                    work.Name = workName;
                    work.Order = w;
                    work.CategoryId = category.Id;
                    works.Add(work);
                }
                else
                {
                    var work = new AssessmentElement
                    {
                        Id = Guid.CreateVersion7(), CategoryId = category.Id, Name = workName, Order = w
                    };
                    Db.AssessmentElements.Add(work);
                    changeLog.Add(ChangeLogEntityType.AssessmentElement, work.Id, ChangeLogAction.Created,
                        [new("name", null, workName)],
                        WorkSheetChangeLog.ElementContext(workName, discipline.Name, actorEmail));
                    works.Add(work);
                }
            }
            category.Works = works;
            orderedManuals.Add(category);
        }

        var attendance = existing.FirstOrDefault(c => c.Kind == AssessmentCategoryKind.Attendance);
        var attendanceWeight = r.AttendanceWeight!.Value;
        if (attendance is null)
        {
            attendance = new AssessmentCategory
            {
                Id = Guid.CreateVersion7(), DisciplineId = disciplineId, Name = AssessmentCategoryNames.Attendance,
                Weight = attendanceWeight, Kind = AssessmentCategoryKind.Attendance, Order = orderedManuals.Count
            };
            Db.AssessmentCategories.Add(attendance);
            changeLog.Add(ChangeLogEntityType.AssessmentCategory, attendance.Id, ChangeLogAction.Created,
                [new("name", null, AssessmentCategoryNames.Attendance), new("weight", null, attendanceWeight)],
                WorkSheetChangeLog.ElementContext(AssessmentCategoryNames.Attendance, discipline.Name, actorEmail));
        }
        else
        {
            if (attendance.Weight != attendanceWeight)
                changeLog.Add(ChangeLogEntityType.AssessmentCategory, attendance.Id, ChangeLogAction.Updated,
                    [new("weight", attendance.Weight, attendanceWeight)],
                    WorkSheetChangeLog.ElementContext(AssessmentCategoryNames.Attendance, discipline.Name, actorEmail));
            attendance.Weight = attendanceWeight;
            attendance.Order = orderedManuals.Count;
        }

        var mode = r.CalculationMode ?? CalculationMode.Weighted;
        var includeAttendance = r.IncludeAttendanceInAverage ?? false;
        var settings = await Db.DisciplineAssessmentSettings.FirstOrDefaultAsync(s => s.DisciplineId == disciplineId, ct);
        var settingChanges = new List<ChangeLogField>();
        if (settings?.AttendanceThreshold != r.AttendanceThreshold)
            settingChanges.Add(new("attendanceThreshold", settings?.AttendanceThreshold, r.AttendanceThreshold));
        if ((settings?.CalculationMode ?? CalculationMode.Weighted) != mode)
            settingChanges.Add(new("calculationMode", settings?.CalculationMode ?? CalculationMode.Weighted, mode));
        if ((settings?.IncludeAttendanceInAverage ?? false) != includeAttendance)
            settingChanges.Add(new("includeAttendanceInAverage", settings?.IncludeAttendanceInAverage ?? false, includeAttendance));
        if (settings is null)
        {
            settings = new DisciplineAssessmentSettings { DisciplineId = disciplineId };
            Db.DisciplineAssessmentSettings.Add(settings);
        }
        if (settingChanges.Count > 0)
            changeLog.Add(ChangeLogEntityType.Discipline, discipline.Id, ChangeLogAction.Updated, settingChanges,
                DisciplineChangeLog.Context(DisciplineChangeLog.From(discipline), actorEmail));
        settings.AttendanceThreshold = r.AttendanceThreshold;
        settings.CalculationMode = mode;
        settings.IncludeAttendanceInAverage = includeAttendance;

        var forCalc = orderedManuals.Concat([attendance]).ToList();
        var ctx = new CalculationContext(mode, includeAttendance);
        foreach (var row in rows) WorkSheetMath.Recalculate(row, forCalc, row.WorkSheet!.TotalLessons, ctx);
        await Db.SaveChangesAsync(ct);
        return await Get(disciplineId, ct);
    }

    private async Task<List<AssessmentCategory>> LoadCategoriesAsync(Guid disciplineId, bool asNoTracking, CancellationToken ct)
    {
        var q = Db.AssessmentCategories.Where(c => c.DisciplineId == disciplineId).Include(c => c.Works).AsQueryable();
        if (asNoTracking) q = q.AsNoTracking();
        var list = await q.ToListAsync(ct);
        foreach (var category in list)
            category.Works = category.Works.OrderBy(w => w.Order).ToList();
        return list.OrderBy(c => c.Kind == AssessmentCategoryKind.Attendance ? 1 : 0).ThenBy(c => c.Order).ToList();
    }

    private Task<DisciplineAssessmentSettings?> SettingsAsync(Guid disciplineId, CancellationToken ct) =>
        Db.DisciplineAssessmentSettings.AsNoTracking().FirstOrDefaultAsync(s => s.DisciplineId == disciplineId, ct);

    private async Task<Dictionary<Guid, int>> ScoreCountsAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        return await Db.WorkSheetScores.AsNoTracking().Where(s => ids.Contains(s.ElementId))
            .GroupBy(s => s.ElementId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }

    internal static AssessmentSettingsDto ToDto(Discipline discipline, List<AssessmentCategory> categories,
        DisciplineAssessmentSettings? settings, Dictionary<Guid, int> counts)
    {
        var attendance = categories.FirstOrDefault(c => c.Kind == AssessmentCategoryKind.Attendance);
        var total = WorkSheetRules.WeightTotal(categories);
        var ctx = WorkSheetMath.ContextOf(settings);
        return new AssessmentSettingsDto(discipline.Id, discipline.Name, settings?.AttendanceThreshold,
            attendance?.Weight ?? 0, ctx.Mode, ctx.IncludeAttendanceInAverage,
            [.. WorkSheetRules.AverageTerms(categories, ctx.IncludeAttendanceInAverage)],
            categories.Select(c => new AssessmentCategoryDto(c.Id, c.Name, c.Weight, c.Kind, c.IncludeInAverage,
                c.Kind == AssessmentCategoryKind.Manual && AssessmentCategoryNames.LooksLikeAttendanceName(c.Name),
                c.Works.Select(w => new AssessmentWorkDto(w.Id, w.Name, counts.GetValueOrDefault(w.Id))).ToList())).ToList(),
            total, WorkSheetRules.HasWeightedCategory(categories) && total == WorkSheetRules.FullWeight,
            WorkSheetRules.TransferReady(categories, ctx));
    }

    private static void Validate(AssessmentSettingsRequest r, Dictionary<Guid, AssessmentCategory> manuals,
        Dictionary<Guid, AssessmentElement> works)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.AttendanceThreshold is decimal t && !WorkSheetRules.HasAtMostTwoDecimals(t))
            errors["attendanceThreshold"] = [FieldCodes.TooManyDecimals];
        if (r.AttendanceWeight is decimal aw && !WorkSheetRules.HasAtMostTwoDecimals(aw))
            errors["attendanceWeight"] = [FieldCodes.TooManyDecimals];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryIds = new HashSet<Guid>();
        var workIds = new HashSet<Guid>();
        for (var i = 0; i < (r.Categories ?? []).Count; i++)
        {
            var c = r.Categories![i];
            if (c.Id is Guid id && (!manuals.ContainsKey(id) || !categoryIds.Add(id)))
                errors[$"categories[{i}].id"] = [FieldCodes.NotFound];
            if (!names.Add(c.Name!.Trim())) errors[$"categories[{i}].name"] = [FieldCodes.Duplicate];
            if (!WorkSheetRules.HasAtMostTwoDecimals(c.Weight!.Value))
                errors[$"categories[{i}].weight"] = [FieldCodes.TooManyDecimals];
            var workNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var w = 0; w < (c.Works ?? []).Count; w++)
            {
                var work = c.Works![w];
                if (work.Id is Guid workId && (!works.ContainsKey(workId) || !workIds.Add(workId)))
                    errors[$"categories[{i}].works[{w}].id"] = [FieldCodes.NotFound];
                if (!workNames.Add(work.Name!.Trim()))
                    errors[$"categories[{i}].works[{w}].name"] = [FieldCodes.Duplicate];
            }
        }
        if (errors.Count > 0) throw AppException.Validation(errors);
    }

    private async Task<string?> ActorEmailAsync()
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }
}

internal static class WorkSheetMath
{
    public static CalculationContext ContextOf(DisciplineAssessmentSettings? settings) =>
        settings is null ? default : new(settings.CalculationMode, settings.IncludeAttendanceInAverage);

    public static CalculationResult Recalculate(WorkSheetRow row, IReadOnlyCollection<AssessmentCategory> categories,
        int? totalLessons, CalculationContext ctx = default)
    {
        var result = WorkSheetRules.Calculate(categories, ScoresOf(row, categories), row.AttendedLessons, totalLessons, ctx);
        row.CalculatedRaw = result.Raw;
        row.CalculatedRounded = result.Rounded;
        return result;
    }

    public static Dictionary<Guid, decimal> ScoresOf(WorkSheetRow? row, IReadOnlyCollection<AssessmentCategory> categories)
    {
        if (row is null) return [];
        var ids = categories.SelectMany(c => c.Works).Select(w => w.Id).ToHashSet();
        return row.Scores.Where(s => ids.Contains(s.ElementId)).ToDictionary(s => s.ElementId, s => s.Value);
    }

    public static Dictionary<Guid, decimal?> CategoryScores(IReadOnlyCollection<AssessmentCategory> categories,
        IReadOnlyDictionary<Guid, decimal> scores)
    {
        var map = new Dictionary<Guid, decimal?>();
        foreach (var category in categories.Where(c => c.Kind == AssessmentCategoryKind.Manual))
            map[category.Id] = WorkSheetRules.CategoryScore(category, scores);
        return map;
    }
}
