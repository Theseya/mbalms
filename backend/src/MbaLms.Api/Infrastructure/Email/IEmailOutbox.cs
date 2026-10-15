namespace MbaLms.Api.Infrastructure.Email;

/// <summary>In-memory store of prepared messages for Development / Testing preview only.</summary>
public interface IEmailOutbox
{
    Guid Add(CapturedEmailMessage message);
    CapturedEmailMessage? Get(Guid id);
    CapturedEmailMessage? LatestFor(string to);
    void Clear();
}

public sealed record CapturedEmailMessage(
    Guid Id,
    DateTimeOffset CapturedAt,
    string To,
    string Subject,
    string TextBody,
    string? HtmlBody,
    string? Purpose);
