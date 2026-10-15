using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Documents;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record TeacherDto(Guid Id, string LastName, string FirstName, string? MiddleName, string FullName, string? Email);

public record TeacherRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string LastName,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string FirstName,
    [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string? MiddleName,
    [EmailAddress(ErrorMessage = FieldCodes.Email)] [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string? Email);

[Route("api/manager/teachers")]
public class TeachersController(
    AppDbContext db, ChangeLogWriter changeLog,
    UserManager<AppUser> users, CurrentUser current) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<TeacherDto>> List(CancellationToken ct) =>
        (await Db.Teachers.AsNoTracking().OrderBy(t => t.LastName).ThenBy(t => t.FirstName).ToListAsync(ct))
        .Select(ToDto).ToList();

    [HttpGet("{id:guid}")]
    public async Task<TeacherDto> Get(Guid id, CancellationToken ct) =>
        ToDto(await Db.Teachers.FindAsync([id], ct) ?? throw AppException.NotFound());

    [HttpPost]
    public async Task<ActionResult<TeacherDto>> Create(TeacherRequest r, CancellationToken ct)
    {
        var t = new Teacher { Id = Guid.CreateVersion7(), LastName = r.LastName.Trim(), FirstName = r.FirstName.Trim() };
        Apply(t, r);
        var snap = TeacherChangeLog.From(t);
        changeLog.Add(
            ChangeLogEntityType.Teacher, t.Id, ChangeLogAction.Created,
            TeacherChangeLog.ForCreate(snap), TeacherChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Teachers.Add(t);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, ToDto(t));
    }

    [HttpPut("{id:guid}")]
    public async Task<TeacherDto> Update(Guid id, TeacherRequest r, CancellationToken ct)
    {
        var t = await Db.Teachers.FindAsync([id], ct) ?? throw AppException.NotFound();
        var before = TeacherChangeLog.From(t);
        Apply(t, r);
        var after = TeacherChangeLog.From(t);
        var changes = TeacherChangeLog.Diff(before, after);
        if (changes.Count == 0)
            return ToDto(t);

        changeLog.Add(
            ChangeLogEntityType.Teacher, t.Id, ChangeLogAction.Updated,
            changes, TeacherChangeLog.Context(after, await ActorEmailAsync()));
        await Db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await Db.Teachers.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (await Db.Lessons.AnyAsync(l => l.TeacherId == id, ct) || await Db.Surveys.AnyAsync(s => s.TeacherId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);
        var snap = TeacherChangeLog.From(t);
        changeLog.Add(
            ChangeLogEntityType.Teacher, t.Id, ChangeLogAction.Deleted,
            TeacherChangeLog.ForDelete(snap), TeacherChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Teachers.Remove(t);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<string?> ActorEmailAsync()
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }

    private static void Apply(Teacher t, TeacherRequest r)
    {
        t.LastName = r.LastName.Trim();
        t.FirstName = r.FirstName.Trim();
        t.MiddleName = Clean(r.MiddleName);
        t.Email = Clean(r.Email);
    }

    private static TeacherDto ToDto(Teacher t) => new(t.Id, t.LastName, t.FirstName, t.MiddleName, t.FullName, t.Email);
}

public record DisciplineDto(Guid Id, string Name, string? Description, DisciplineProgramDto? Program = null);

public record DisciplineRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(200, ErrorMessage = FieldCodes.MaxLength)] string Name,
    [MaxLength(2000, ErrorMessage = FieldCodes.MaxLength)] string? Description);

[Route("api/manager/disciplines")]
public class DisciplinesController(
    AppDbContext db, ChangeLogWriter changeLog,
    UserManager<AppUser> users, CurrentUser current, DocumentStorage documents) : ManagerControllerBase(db)
{
    [HttpGet]
    public Task<List<DisciplineDto>> List(CancellationToken ct) =>
        WithPrograms(Db.Disciplines.AsNoTracking().OrderBy(d => d.Name)).ToListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<DisciplineDto> Get(Guid id, CancellationToken ct) =>
        await WithPrograms(Db.Disciplines.AsNoTracking().Where(d => d.Id == id)).FirstOrDefaultAsync(ct)
        ?? throw AppException.NotFound();

    private IQueryable<DisciplineDto> WithPrograms(IQueryable<Discipline> disciplines) =>
        from d in disciplines
        join p in Db.DisciplinePrograms on d.Id equals p.DisciplineId into programs
        from p in programs.DefaultIfEmpty()
        select new DisciplineDto(d.Id, d.Name, d.Description,
            p == null ? null : new DisciplineProgramDto(p.FileName, p.SizeBytes, p.UploadedAt));

    [HttpPost]
    public async Task<ActionResult<DisciplineDto>> Create(DisciplineRequest r, CancellationToken ct)
    {
        await EnsureUniqueNameAsync(r.Name, null, ct);
        var d = new Discipline { Id = Guid.CreateVersion7(), Name = r.Name.Trim(), Description = Clean(r.Description) };
        var snap = DisciplineChangeLog.From(d);
        changeLog.Add(
            ChangeLogEntityType.Discipline, d.Id, ChangeLogAction.Created,
            DisciplineChangeLog.ForCreate(snap), DisciplineChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Disciplines.Add(d);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = d.Id }, new DisciplineDto(d.Id, d.Name, d.Description));
    }

    [HttpPut("{id:guid}")]
    public async Task<DisciplineDto> Update(Guid id, DisciplineRequest r, CancellationToken ct)
    {
        var d = await Db.Disciplines.FindAsync([id], ct) ?? throw AppException.NotFound();
        await EnsureUniqueNameAsync(r.Name, id, ct);
        var before = DisciplineChangeLog.From(d);
        d.Name = r.Name.Trim();
        d.Description = Clean(r.Description);
        var after = DisciplineChangeLog.From(d);
        var changes = DisciplineChangeLog.Diff(before, after);
        if (changes.Count > 0)
        {
            changeLog.Add(
                ChangeLogEntityType.Discipline, d.Id, ChangeLogAction.Updated,
                changes, DisciplineChangeLog.Context(after, await ActorEmailAsync()));
            await Db.SaveChangesAsync(ct);
        }
        return await Get(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var d = await Db.Disciplines.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (await Db.Lessons.AnyAsync(l => l.DisciplineId == id, ct) || await Db.Grades.AnyAsync(g => g.DisciplineId == id, ct)
            || await Db.Surveys.AnyAsync(s => s.DisciplineId == id, ct) || await Db.WorkSheets.AnyAsync(s => s.DisciplineId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);
        var snap = DisciplineChangeLog.From(d);
        changeLog.Add(
            ChangeLogEntityType.Discipline, d.Id, ChangeLogAction.Deleted,
            DisciplineChangeLog.ForDelete(snap), DisciplineChangeLog.Context(snap, await ActorEmailAsync()));
        // The programme row goes with the discipline (cascade); its file is removed once that is committed.
        var programKey = await Db.DisciplinePrograms.Where(p => p.DisciplineId == id)
            .Select(p => (Guid?)p.StorageKey).FirstOrDefaultAsync(ct);
        Db.Disciplines.Remove(d);
        await Db.SaveChangesAsync(ct);
        if (programKey is Guid key) documents.Delete(key);
        return NoContent();
    }

    private async Task EnsureUniqueNameAsync(string name, Guid? id, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (await Db.Disciplines.AnyAsync(d => d.Id != id && d.Name.ToLower() == trimmed.ToLower(), ct))
            throw AppException.Validation(nameof(DisciplineRequest.Name), ErrorCodes.Duplicate);
    }

    private async Task<string?> ActorEmailAsync()
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }
}

public record PeriodDto(Guid Id, string Name, DateOnly? StartDate, DateOnly? EndDate);

public record PeriodRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string Name,
    DateOnly? StartDate,
    DateOnly? EndDate);

[Route("api/manager/periods")]
public class PeriodsController(
    AppDbContext db, ChangeLogWriter changeLog,
    UserManager<AppUser> users, CurrentUser current) : ManagerControllerBase(db)
{
    [HttpGet]
    public Task<List<PeriodDto>> List(CancellationToken ct) =>
        Db.Periods.AsNoTracking().OrderByDescending(p => p.StartDate).ThenBy(p => p.Name)
            .Select(p => new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate)).ToListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<PeriodDto> Get(Guid id, CancellationToken ct)
    {
        var p = await Db.Periods.FindAsync([id], ct) ?? throw AppException.NotFound();
        return new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate);
    }

    [HttpPost]
    public async Task<ActionResult<PeriodDto>> Create(PeriodRequest r, CancellationToken ct)
    {
        Validate(r);
        var p = new AcademicPeriod { Id = Guid.CreateVersion7(), Name = r.Name.Trim(), StartDate = r.StartDate, EndDate = r.EndDate };
        var snap = AcademicPeriodChangeLog.From(p);
        changeLog.Add(
            ChangeLogEntityType.AcademicPeriod, p.Id, ChangeLogAction.Created,
            AcademicPeriodChangeLog.ForCreate(snap), AcademicPeriodChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Periods.Add(p);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate));
    }

    [HttpPut("{id:guid}")]
    public async Task<PeriodDto> Update(Guid id, PeriodRequest r, CancellationToken ct)
    {
        Validate(r);
        var p = await Db.Periods.FindAsync([id], ct) ?? throw AppException.NotFound();
        var before = AcademicPeriodChangeLog.From(p);
        p.Name = r.Name.Trim();
        p.StartDate = r.StartDate;
        p.EndDate = r.EndDate;
        var after = AcademicPeriodChangeLog.From(p);
        var changes = AcademicPeriodChangeLog.Diff(before, after);
        if (changes.Count == 0)
            return new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate);

        changeLog.Add(
            ChangeLogEntityType.AcademicPeriod, p.Id, ChangeLogAction.Updated,
            changes, AcademicPeriodChangeLog.Context(after, await ActorEmailAsync()));
        await Db.SaveChangesAsync(ct);
        return new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await Db.Periods.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (await Db.Grades.AnyAsync(g => g.PeriodId == id, ct) || await Db.WorkSheets.AnyAsync(s => s.PeriodId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);
        var snap = AcademicPeriodChangeLog.From(p);
        changeLog.Add(
            ChangeLogEntityType.AcademicPeriod, p.Id, ChangeLogAction.Deleted,
            AcademicPeriodChangeLog.ForDelete(snap), AcademicPeriodChangeLog.Context(snap, await ActorEmailAsync()));
        Db.Periods.Remove(p);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Validate(PeriodRequest r)
    {
        if (r.StartDate is not null && r.EndDate is not null && r.EndDate < r.StartDate)
            throw AppException.Validation(nameof(r.EndDate), FieldCodes.EndBeforeStart);
    }

    private async Task<string?> ActorEmailAsync()
    {
        var user = await users.FindByIdAsync(current.UserId.ToString());
        return user?.Email ?? user?.UserName;
    }
}
