using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
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
public class TeachersController(AppDbContext db) : ManagerControllerBase(db)
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
        Db.Teachers.Add(t);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, ToDto(t));
    }

    [HttpPut("{id:guid}")]
    public async Task<TeacherDto> Update(Guid id, TeacherRequest r, CancellationToken ct)
    {
        var t = await Db.Teachers.FindAsync([id], ct) ?? throw AppException.NotFound();
        Apply(t, r);
        await Db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await Db.Teachers.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (await Db.Lessons.AnyAsync(l => l.TeacherId == id, ct) || await Db.Surveys.AnyAsync(s => s.TeacherId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);
        Db.Teachers.Remove(t);
        await Db.SaveChangesAsync(ct);
        return NoContent();
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

public record DisciplineDto(Guid Id, string Name, string? Description);

public record DisciplineRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(200, ErrorMessage = FieldCodes.MaxLength)] string Name,
    [MaxLength(2000, ErrorMessage = FieldCodes.MaxLength)] string? Description);

[Route("api/manager/disciplines")]
public class DisciplinesController(AppDbContext db) : ManagerControllerBase(db)
{
    [HttpGet]
    public Task<List<DisciplineDto>> List(CancellationToken ct) =>
        Db.Disciplines.AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new DisciplineDto(d.Id, d.Name, d.Description)).ToListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<DisciplineDto> Get(Guid id, CancellationToken ct)
    {
        var d = await Db.Disciplines.FindAsync([id], ct) ?? throw AppException.NotFound();
        return new DisciplineDto(d.Id, d.Name, d.Description);
    }

    [HttpPost]
    public async Task<ActionResult<DisciplineDto>> Create(DisciplineRequest r, CancellationToken ct)
    {
        var d = new Discipline { Id = Guid.CreateVersion7(), Name = r.Name.Trim(), Description = Clean(r.Description) };
        Db.Disciplines.Add(d);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = d.Id }, new DisciplineDto(d.Id, d.Name, d.Description));
    }

    [HttpPut("{id:guid}")]
    public async Task<DisciplineDto> Update(Guid id, DisciplineRequest r, CancellationToken ct)
    {
        var d = await Db.Disciplines.FindAsync([id], ct) ?? throw AppException.NotFound();
        d.Name = r.Name.Trim();
        d.Description = Clean(r.Description);
        await Db.SaveChangesAsync(ct);
        return new DisciplineDto(d.Id, d.Name, d.Description);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var d = await Db.Disciplines.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (await Db.Lessons.AnyAsync(l => l.DisciplineId == id, ct) || await Db.Grades.AnyAsync(g => g.DisciplineId == id, ct)
            || await Db.Surveys.AnyAsync(s => s.DisciplineId == id, ct))
            throw AppException.Conflict(ErrorCodes.InUse);
        Db.Disciplines.Remove(d);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record PeriodDto(Guid Id, string Name, DateOnly? StartDate, DateOnly? EndDate);

public record PeriodRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string Name,
    DateOnly? StartDate,
    DateOnly? EndDate);

[Route("api/manager/periods")]
public class PeriodsController(AppDbContext db) : ManagerControllerBase(db)
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
        Db.Periods.Add(p);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id }, new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate));
    }

    [HttpPut("{id:guid}")]
    public async Task<PeriodDto> Update(Guid id, PeriodRequest r, CancellationToken ct)
    {
        Validate(r);
        var p = await Db.Periods.FindAsync([id], ct) ?? throw AppException.NotFound();
        p.Name = r.Name.Trim();
        p.StartDate = r.StartDate;
        p.EndDate = r.EndDate;
        await Db.SaveChangesAsync(ct);
        return new PeriodDto(p.Id, p.Name, p.StartDate, p.EndDate);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await Db.Periods.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (await Db.Grades.AnyAsync(g => g.PeriodId == id, ct)) throw AppException.Conflict(ErrorCodes.InUse);
        Db.Periods.Remove(p);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Validate(PeriodRequest r)
    {
        if (r.StartDate is not null && r.EndDate is not null && r.EndDate < r.StartDate)
            throw AppException.Validation(nameof(r.EndDate), FieldCodes.EndBeforeStart);
    }
}
