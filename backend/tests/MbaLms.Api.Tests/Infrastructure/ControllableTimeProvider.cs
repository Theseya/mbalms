namespace MbaLms.Api.Tests.Infrastructure;

/// <summary>
/// Controllable clock for expiry tests. By default follows the system clock so unrelated
/// tests keep distinct timestamps; call SetUtcNow to freeze/control time.
/// </summary>
public sealed class ControllableTimeProvider : TimeProvider
{
    private DateTimeOffset? _fixed;

    public override DateTimeOffset GetUtcNow() => _fixed ?? TimeProvider.System.GetUtcNow();

    public void SetUtcNow(DateTimeOffset utcNow) => _fixed = utcNow.ToUniversalTime();

    public void Advance(TimeSpan delta) => _fixed = GetUtcNow() + delta;

    public void ResetToSystem() => _fixed = null;
}
