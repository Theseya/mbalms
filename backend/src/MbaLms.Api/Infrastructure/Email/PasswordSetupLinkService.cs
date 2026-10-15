using MbaLms.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MbaLms.Api.Infrastructure.Email;

/// <summary>Builds Identity password-reset tokens and public set-password URLs. Never logs the token.</summary>
public sealed class PasswordSetupLinkService(
    UserManager<AppUser> users,
    IOptions<EmailOptions> options)
{
    public async Task<(string Token, string SetupUrl)> CreateResetLinkAsync(AppUser user)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var email = user.Email ?? user.UserName
            ?? throw new InvalidOperationException("User has no email.");
        var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
        var setupUrl =
            $"{baseUrl}/set-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        return (token, setupUrl);
    }
}
