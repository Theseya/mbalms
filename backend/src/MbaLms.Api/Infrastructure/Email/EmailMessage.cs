namespace MbaLms.Api.Infrastructure.Email;

/// <summary>Prepared outbound message. Delivery is separate from composition.</summary>
public sealed record EmailMessage(
    string To,
    string Subject,
    string TextBody,
    string? HtmlBody = null,
    string? Purpose = null);
