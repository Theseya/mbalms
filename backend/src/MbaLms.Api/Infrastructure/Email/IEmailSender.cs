namespace MbaLms.Api.Infrastructure.Email;

/// <summary>
/// Delivery port for prepared messages. Swap implementations (Development capture → SMTP)
/// without changing invite / forgot-password flows.
/// </summary>
public interface IEmailSender
{
    /// <summary>True when this sender can accept messages (Development capture or future SMTP).</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
