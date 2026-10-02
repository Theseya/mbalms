using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers.Manager;

[ApiController]
[Authorize(Policy = Roles.Manager)]
public abstract class ManagerControllerBase(AppDbContext db) : ControllerBase
{
    protected AppDbContext Db { get; } = db;

    /// <summary>Archived groups are read-only for new academic records.</summary>
    protected async Task<Group> RequireActiveGroupAsync(Guid? groupId, string field, CancellationToken ct)
    {
        if (groupId is null) throw AppException.Validation(field, FieldCodes.Required);
        var group = await Db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw AppException.Validation(field, FieldCodes.NotFound);
        if (group.Status == GroupStatus.Archived) throw AppException.Conflict(ErrorCodes.GroupArchived);
        return group;
    }

    protected async Task RequireExistsAsync<T>(Guid? id, string field, CancellationToken ct) where T : class
    {
        if (id is null) throw AppException.Validation(field, FieldCodes.Required);
        if (await Db.Set<T>().FindAsync([id.Value], ct) is null)
            throw AppException.Validation(field, FieldCodes.NotFound);
    }

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
