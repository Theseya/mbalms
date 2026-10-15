namespace MbaLms.Api.Infrastructure.Email;

public class EmailOptions
{
    public const string Section = "Email";

    /// <summary>
    /// <c>None</c> — delivery unavailable (Production default; callers get a clear error).
    /// <c>Development</c> — capture prepared messages for local preview only (no SMTP).
    /// Future: <c>Smtp</c> without changing invite/reset call sites.
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>Public web origin used in password-setup links (no trailing slash).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Identity password-reset / invite token lifetime.</summary>
    public TimeSpan TokenLifespan { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Unused by senders. Omit from committed JSON. Optional local override: Email__FutureTestRecipient.
    /// Null/absent is the safe default. Must never replace participant addresses in the database.
    /// </summary>
    public string? FutureTestRecipient { get; set; }
}
