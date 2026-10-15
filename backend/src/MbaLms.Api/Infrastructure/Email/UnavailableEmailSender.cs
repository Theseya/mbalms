namespace MbaLms.Api.Infrastructure.Email;

/// <summary>Production-safe stub when no mail provider is configured.</summary>
public sealed class UnavailableEmailSender : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        throw AppException.ServiceUnavailable(ErrorCodes.MailNotConfigured);
}
