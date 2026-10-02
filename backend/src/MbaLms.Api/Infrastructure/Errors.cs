using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MbaLms.Api.Infrastructure;

/// <summary>
/// Stable machine-readable error codes. The client maps them to localized messages.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string InUse = "in_use";
    public const string Duplicate = "duplicate";
    public const string GroupArchived = "group_archived";
    public const string InvalidCredentials = "invalid_credentials";
    public const string LockedOut = "locked_out";
    public const string SurveyNotEditable = "survey_not_editable";
    public const string SurveyNotAccepting = "survey_not_accepting";
    public const string SurveyAlreadySubmitted = "survey_already_submitted";
    public const string SurveyHasNoQuestions = "survey_has_no_questions";
    public const string InvalidStatusTransition = "invalid_status_transition";
    public const string CsrfFailed = "csrf_failed";
    public const string Forbidden = "forbidden";
    public const string ServerError = "server_error";
}

/// <summary>Field-level validation error codes (used as values in the "errors" dictionary).</summary>
public static class FieldCodes
{
    public const string Required = "required";
    public const string Range = "range";
    public const string MaxLength = "max_length";
    public const string Invalid = "invalid";
    public const string Email = "email";
    public const string EndBeforeStart = "end_before_start";
    public const string NotFound = "not_found";
    public const string PasswordWeak = "password_weak";
}

public class AppException(int status, string code, IDictionary<string, string[]>? errors = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IDictionary<string, string[]>? Errors { get; } = errors;

    public static AppException NotFound() => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound);
    public static AppException Conflict(string code) => new(StatusCodes.Status409Conflict, code);
    public static AppException BadRequest(string code) => new(StatusCodes.Status400BadRequest, code);

    /// <param name="field">C# property name or path; converted to camelCase to match JSON.</param>
    public static AppException Validation(string field, string fieldCode) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
            new Dictionary<string, string[]> { [JsonNamingPolicy.CamelCase.ConvertName(field)] = [fieldCode] });

    public static AppException Validation(IDictionary<string, string[]> errors) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, errors);
}

/// <summary>
/// Converts exceptions to RFC 7807 problem details. Never exposes stack traces or exception messages.
/// </summary>
public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger, IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        int status;
        string code;
        IDictionary<string, string[]>? errors = null;

        switch (exception)
        {
            case AppException app:
                status = app.Status;
                code = app.Code;
                errors = app.Errors;
                break;
            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }:
                status = StatusCodes.Status409Conflict;
                code = ErrorCodes.Duplicate;
                break;
            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } }:
                status = StatusCodes.Status409Conflict;
                code = ErrorCodes.InUse;
                break;
            case DbUpdateConcurrencyException:
                status = StatusCodes.Status409Conflict;
                code = ErrorCodes.Conflict;
                break;
            case BadHttpRequestException bad:
                status = bad.StatusCode;
                code = ErrorCodes.ValidationFailed;
                break;
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
                status = StatusCodes.Status500InternalServerError;
                code = ErrorCodes.ServerError;
                break;
        }

        http.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = code };
        pd.Extensions["code"] = code;
        if (errors is not null) pd.Extensions["errors"] = errors;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = pd
        });
    }
}
