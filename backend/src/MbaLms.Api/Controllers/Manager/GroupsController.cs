using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

public record GroupDto(Guid Id, string Name, DateOnly? StartDate, DateOnly? EndDate, GroupStatus Status,
    DateTimeOffset? ArchivedAt, int StudentCount);

public record GroupRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(100, ErrorMessage = FieldCodes.MaxLength)] string Name,
    DateOnly? StartDate,
    DateOnly? EndDate);

public enum GroupFilter { Active, Archived, All }

[Route("api/manager/groups")]
public class GroupsController(AppDbContext db, AppTime time) : ManagerControllerBase(db)
{
    [HttpGet]
    public async Task<List<GroupDto>> List([FromQuery] GroupFilter status = GroupFilter.Active, CancellationToken ct = default)
    {
        var q = Db.Groups.AsNoTracking();
        q = status switch
        {
            GroupFilter.Active => q.Where(g => g.Status == GroupStatus.Active),
            GroupFilter.Archived => q.Where(g => g.Status == GroupStatus.Archived),
            _ => q
        };
        return await q.OrderBy(g => g.Status).ThenBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, g.StartDate, g.EndDate, g.Status, g.ArchivedAt, g.Students.Count))
            .ToListAsync(ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<GroupDto> Get(Guid id, CancellationToken ct)
    {
        return await Db.Groups.AsNoTracking().Where(g => g.Id == id)
                   .Select(g => new GroupDto(g.Id, g.Name, g.StartDate, g.EndDate, g.Status, g.ArchivedAt, g.Students.Count))
                   .FirstOrDefaultAsync(ct)
               ?? throw AppException.NotFound();
    }

    [HttpPost]
    public async Task<ActionResult<GroupDto>> Create(GroupRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var programId = await Db.Programs.Select(p => p.Id).FirstAsync(ct);
        var group = new Group
        {
            Id = Guid.CreateVersion7(),
            ProgramId = programId,
            Name = request.Name.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate
        };
        Db.Groups.Add(group);
        await Db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = group.Id }, await Get(group.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<GroupDto> Update(Guid id, GroupRequest request, CancellationToken ct)
    {
        var group = await Db.Groups.FindAsync([id], ct) ?? throw AppException.NotFound();
        await ValidateAsync(request, id, ct);
        group.Name = request.Name.Trim();
        group.StartDate = request.StartDate;
        group.EndDate = request.EndDate;
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Soft archive: only the status changes, all related data is kept.</summary>
    [HttpPost("{id:guid}/archive")]
    public async Task<GroupDto> Archive(Guid id, CancellationToken ct)
    {
        var group = await Db.Groups.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (group.Status == GroupStatus.Archived) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        group.Status = GroupStatus.Archived;
        group.ArchivedAt = time.UtcNow;
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<GroupDto> Restore(Guid id, CancellationToken ct)
    {
        var group = await Db.Groups.FindAsync([id], ct) ?? throw AppException.NotFound();
        if (group.Status == GroupStatus.Active) throw AppException.Conflict(ErrorCodes.InvalidStatusTransition);
        group.Status = GroupStatus.Active;
        group.ArchivedAt = null;
        await Db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Only an empty group can be deleted; groups with data must be archived instead.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var group = await Db.Groups.FindAsync([id], ct) ?? throw AppException.NotFound();
        var hasData = await Db.Students.AnyAsync(s => s.GroupId == id, ct)
                      || await Db.Lessons.AnyAsync(l => l.GroupId == id, ct)
                      || await Db.Surveys.AnyAsync(s => s.GroupId == id, ct);
        if (hasData) throw AppException.Conflict(ErrorCodes.InUse);
        Db.Groups.Remove(group);
        await Db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task ValidateAsync(GroupRequest r, Guid? id, CancellationToken ct)
    {
        if (r.StartDate is not null && r.EndDate is not null && r.EndDate < r.StartDate)
            throw AppException.Validation(nameof(r.EndDate), FieldCodes.EndBeforeStart);
        var name = r.Name.Trim();
        if (await Db.Groups.AnyAsync(g => g.Id != id && g.Name.ToLower() == name.ToLower(), ct))
            throw AppException.Validation(nameof(r.Name), ErrorCodes.Duplicate);
    }
}
