using System.ComponentModel.DataAnnotations;
using MbaLms.Api.Data;
using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Email;
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

public record ForgotPasswordRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [EmailAddress(ErrorMessage = FieldCodes.Email)]
    [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string Email);

public record SetPasswordRequest(
    [Required(ErrorMessage = FieldCodes.Required)] [EmailAddress(ErrorMessage = FieldCodes.Email)]
    [MaxLength(256, ErrorMessage = FieldCodes.MaxLength)] string Email,
    [Required(ErrorMessage = FieldCodes.Required)] [MaxLength(2048, ErrorMessage = FieldCodes.MaxLength)] string Token,
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
    AppTime time,
    IEmailSender emailSender,
    PasswordSetupLinkService passwordLinks) : ControllerBase
{
    private static readonly AppUser DummyUser = new();
    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<AppUser>().HashPassword(DummyUser, Guid.NewGuid().ToString()));

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
        if (user is null)
        {
            // Spend the same hashing time as for a real account so response time does not reveal registered emails.
            users.PasswordHasher.VerifyHashedPassword(DummyUser, DummyHash.Value, request.Password);
            throw new AppException(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials);
        }

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

    /// <summary>
    /// When mail is configured: always No Content (does not reveal whether the email is registered).
    /// When mail is not configured: 503 <see cref="ErrorCodes.MailNotConfigured"/> — never pretend the letter was sent.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("password-mail")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        if (!emailSender.IsConfigured)
            throw AppException.ServiceUnavailable(ErrorCodes.MailNotConfigured);

        var email = request.Email.Trim();
        var user = await users.FindByEmailAsync(email);
        if (user is not null)
        {
            var (_, setupUrl) = await passwordLinks.CreateResetLinkAsync(user);
            await emailSender.SendAsync(PasswordEmailComposer.PasswordReset(email, setupUrl), ct);
        }
        else
        {
            // Same hashing cost as a failed login probe — avoid timing that reveals registration.
            users.PasswordHasher.VerifyHashedPassword(DummyUser, DummyHash.Value, Guid.NewGuid().ToString());
        }

        return NoContent();
    }

    /// <summary>Sets a new password using a one-time Identity reset token (invite or forgot-password).</summary>
    [HttpPost("set-password")]
    [AllowAnonymous]
    [EnableRateLimiting("password-mail")]
    public async Task<IActionResult> SetPassword(SetPasswordRequest request)
    {
        var email = request.Email.Trim();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
            throw AppException.BadRequest(ErrorCodes.InvalidPasswordToken);

        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)))
                throw AppException.Validation(nameof(request.NewPassword), FieldCodes.PasswordWeak);
            throw AppException.BadRequest(ErrorCodes.InvalidPasswordToken);
        }

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
