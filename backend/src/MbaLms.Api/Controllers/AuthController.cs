using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace MbaLms.Api.Controllers;

public record LoginRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string Email,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string Password);

public record ChangePasswordRequest(
    [Required(ErrorMessage = FieldCodes.Required)] string CurrentPassword,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string NewPassword);

public record MeDto(Guid Id, string Email, string Role, string DisplayName, string? GroupName, string TimeZone);

public record CsrfDto(string Token);

[ApiController]
[Route("api/auth")]
public class AuthController(
    SignInManager<AppUser> signIn,
    UserManager<AppUser> users,
    IAntiforgery antiforgery,
    AppDbContext db,
    AppTime time) : ControllerBase
{
    /// <summary>
    /// Issues an antiforgery token. The client sends it in the X-XSRF-TOKEN header on every
    /// state-changing request. Must be requested again after login/logout (token is bound to the user).
    /// </summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public CsrfDto Csrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new CsrfDto(tokens.RequestToken!);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<MeDto>> Login(LoginRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null) throw new AppException(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials);

        var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.IsLockedOut) throw new AppException(StatusCodes.Status401Unauthorized, ErrorCodes.LockedOut);
        if (!result.Succeeded) throw new AppException(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials);

        return await BuildMe(user);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeDto>> Me()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Unauthorized();
        return await BuildMe(user);
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await users.GetUserAsync(User) ?? throw new UnauthorizedAccessException();
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var field = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
                ? nameof(request.CurrentPassword)
                : nameof(request.NewPassword);
            var code = field == nameof(request.CurrentPassword) ? FieldCodes.Invalid : FieldCodes.PasswordWeak;
            throw AppException.Validation(field, code);
        }
        await signIn.RefreshSignInAsync(user);
        return NoContent();
    }

    private async Task<MeDto> BuildMe(AppUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var role = roles.Contains(Roles.Manager) ? Roles.Manager : Roles.Student;
        string displayName = user.Email!;
        string? groupName = null;
        if (role == Roles.Student)
        {
            var student = await db.Students.AsNoTracking().Include(s => s.Group).FirstOrDefaultAsync(s => s.UserId == user.Id);
            if (student is not null)
            {
                displayName = $"{student.FirstName} {student.LastName}";
                groupName = student.Group?.Name;
            }
        }
        return new MeDto(user.Id, user.Email!, role, displayName, groupName, time.TimeZoneId);
    }
}
