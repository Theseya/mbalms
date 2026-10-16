using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MbaLms.Api.Controllers.Admin;

[ApiController]
[Route("api/admin/audit")]
[Authorize(Policy = Roles.StaffAdminPolicy)]
public class AdminAuditController(StaffAccessService staff) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AdminAuditDto>> List(
        [FromQuery] [Range(1, int.MaxValue, ErrorMessage = FieldCodes.Range)] int page = 1,
        [FromQuery] [Range(1, Paging.MaxPageSize, ErrorMessage = FieldCodes.Range)] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default) =>
        staff.ListAuditAsync(page, pageSize, ct);
}
