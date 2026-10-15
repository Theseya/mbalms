namespace MbaLms.Api.Infrastructure.Email;

/// <summary>
/// Captures prepared mail for local preview. Does not send over the network.
/// Must not be registered in Production.
/// </summary>
public sealed class DevelopmentEmailSender(IEmailOutbox outbox, TimeProvider clock) : IEmailSender
{
    public bool IsConfigured => true;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        outbox.Add(new CapturedEmailMessage(
            Guid.CreateVersion7(),
            clock.GetUtcNow(),
            message.To,
            message.Subject,
            message.TextBody,
            message.HtmlBody,
            message.Purpose));
        return Task.CompletedTask;
    }
}
