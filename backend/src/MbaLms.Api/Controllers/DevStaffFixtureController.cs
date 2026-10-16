using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MbaLms.Api.Controllers;

public record StaffFixtureRequest(
    [Required(ErrorMessage = FieldCodes.Required)]
    [EmailAddress(ErrorMessage = FieldCodes.Email)]
    [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)]
    string Email,
    [Required(ErrorMessage = FieldCodes.Required)]
    [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)]
    string Password,
    [Required(ErrorMessage = FieldCodes.Required)]
    [MaxLength(32, ErrorMessage = FieldCodes.MaxLength)]
    string Role);

public record StaffFixtureDto(Guid Id, string Email, string Role);

/// <summary>
/// Test-only helper to create Owner / AccountAdmin with synthetic credentials.
/// Registered only when <see cref="StaffFixtureAvailability"/> is enabled; never in Production.
/// </summary>
[ApiController]
[Route("api/dev/staff-fixture")]
[Authorize(Policy = Roles.Manager)]
public class DevStaffFixtureController(
    UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IHostEnvironment env,
    IConfiguration config) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<StaffFixtureDto>> Create(StaffFixtureRequest request, CancellationToken ct)
    {
        if (!StaffFixtureAvailability.IsEnabled(env, config))
            throw AppException.NotFound();

        var email = request.Email.Trim();
        var role = request.Role.Trim();
        if (role is not (Roles.Owner or Roles.AccountAdmin))
            throw AppException.Validation(nameof(request.Role), FieldCodes.Invalid);

        if (!await roles.RoleExistsAsync(role))
            await roles.CreateAsync(new IdentityRole<Guid>(role));

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            var existingRoles = await users.GetRolesAsync(existing);
            if (!existingRoles.Contains(role) || existingRoles.Count != 1)
                throw AppException.Conflict(ErrorCodes.Duplicate);
            return new StaffFixtureDto(existing.Id, existing.Email!, role);
        }

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            LockoutEnabled = true
        };
        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)))
                throw AppException.Validation(nameof(request.Password), FieldCodes.PasswordWeak);
            throw AppException.Validation(nameof(request.Email), FieldCodes.Invalid);
        }
        var added = await users.AddToRoleAsync(user, role);
        if (!added.Succeeded) throw AppException.Validation(nameof(request.Role), FieldCodes.Invalid);
        return Created(string.Empty, new StaffFixtureDto(user.Id, email, role));
    }
}
