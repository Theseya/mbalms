namespace MbaLms.Api.Infrastructure;

public class AppOptions
{
    public const string Section = "App";

    /// <summary>IANA time zone in which schedule times are entered and displayed.</summary>
    public string TimeZone { get; set; } = "Europe/Moscow";
}

/// <summary>
/// Time strategy: every instant is stored in the database as UTC (timestamptz).
/// The API accepts schedule/survey times as wall-clock values in the application
/// time zone (no offset) and returns both the UTC instant and the local wall-clock value.
/// </summary>
public class AppTime
{
    private readonly TimeProvider _clock;

    public AppTime(Microsoft.Extensions.Options.IOptions<AppOptions> options, TimeProvider clock)
    {
        _clock = clock;
        TimeZoneId = options.Value.TimeZone;
        Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

    public string TimeZoneId { get; }
    public TimeZoneInfo Zone { get; }

    public DateTimeOffset UtcNow => _clock.GetUtcNow();

    /// <summary>Interprets a wall-clock value in the application time zone and returns the UTC instant.</summary>
    public DateTimeOffset ToUtc(DateTime local, string field)
    {
        // A value with "Z" or an offset would be silently shifted by the zone difference, so it is rejected.
        if (local.Kind != DateTimeKind.Unspecified) throw AppException.Validation(field, FieldCodes.Invalid);
        var unspecified = local;
        if (Zone.IsInvalidTime(unspecified))
            throw AppException.Validation(field, FieldCodes.Invalid);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public DateTimeOffset? ToUtc(DateTime? local, string field) => local is null ? null : ToUtc(local.Value, field);

    public DateTime ToLocal(DateTimeOffset utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(utc, Zone).DateTime, DateTimeKind.Unspecified);

    public DateTime? ToLocal(DateTimeOffset? utc) => utc is null ? null : ToLocal(utc.Value);
}
