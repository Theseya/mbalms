using MbaLms.Api.Domain;
using MbaLms.Api.Infrastructure;
using MbaLms.Api.Infrastructure.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MbaLms.Api.Controllers;

public record CapturedEmailDto(
    Guid Id, DateTimeOffset CapturedAt, string To, string Subject, string TextBody, string? Purpose);

/// <summary>
/// Local preview of prepared (not delivered) mail. Available only in Development and Testing.
/// </summary>
[ApiController]
[Route("api/dev/mail")]
[Authorize(Roles = $"{Roles.Manager},{Roles.Owner},{Roles.AccountAdmin}")]
public class DevMailController(IEmailOutbox outbox, IHostEnvironment env) : ControllerBase
{
    private void EnsureDevOnly()
    {
        if (!(env.IsDevelopment() || env.IsEnvironment("Testing")))
            throw AppException.NotFound();
    }

    [HttpGet("{id:guid}")]
    public CapturedEmailDto Get(Guid id)
    {
        EnsureDevOnly();
        var m = outbox.Get(id) ?? throw AppException.NotFound();
        return ToDto(m);
    }

    [HttpGet("latest")]
    public CapturedEmailDto Latest([FromQuery] string to)
    {
        EnsureDevOnly();
        if (string.IsNullOrWhiteSpace(to)) throw AppException.Validation(nameof(to), FieldCodes.Required);
        var m = outbox.LatestFor(to.Trim()) ?? throw AppException.NotFound();
        return ToDto(m);
    }

    private static CapturedEmailDto ToDto(CapturedEmailMessage m) =>
        new(m.Id, m.CapturedAt, m.To, m.Subject, m.TextBody, m.Purpose);
}
