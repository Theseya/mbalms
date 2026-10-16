using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Controllers.Manager;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MbaLms.Api.Controllers.Admin;

public record InviteManagerRequest(
    [Required(ErrorMessage = FieldCodes.Required)]
    [EmailAddress(ErrorMessage = FieldCodes.Email)]
    [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)]
    string Email);

[ApiController]
[Route("api/admin/staff")]
[Authorize(Policy = Roles.StaffAdminPolicy)]
public class AdminStaffController(StaffAccessService staff) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<StaffDto>> List(
        [FromQuery] [MaxLength(Paging.MaxSearchLength, ErrorMessage = FieldCodes.MaxLength)] string? search = null,
        [FromQuery] [Range(1, int.MaxValue, ErrorMessage = FieldCodes.Range)] int page = 1,
        [FromQuery] [Range(1, Paging.MaxPageSize, ErrorMessage = FieldCodes.Range)] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default) =>
        staff.ListAsync(search, page, pageSize, ct);

    [HttpPost("managers")]
    [EnableRateLimiting("password-mail")]
    public Task<StaffInviteResultDto> InviteManager(InviteManagerRequest request, CancellationToken ct) =>
        staff.InviteManagerAsync(request.Email, ct);

    [HttpPost("{id:guid}/block")]
    public async Task<IActionResult> Block(Guid id, CancellationToken ct)
    {
        await staff.BlockAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/unblock")]
    public async Task<IActionResult> Unblock(Guid id, CancellationToken ct)
    {
        await staff.UnblockAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/revoke-owner")]
    public async Task<IActionResult> RevokeOwner(Guid id, CancellationToken ct)
    {
        await staff.RevokeOwnerAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await staff.DeleteAsync(id, ct);
        return NoContent();
    }
}
