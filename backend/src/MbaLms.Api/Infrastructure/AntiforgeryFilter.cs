using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MbaLms.Api.Infrastructure;

/// <summary>
/// Validates the antiforgery token (X-XSRF-TOKEN header) on every state-changing API request.
/// Actions marked with [IgnoreAntiforgeryToken] are skipped.
/// </summary>
public sealed class ValidateAntiforgeryFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
            return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<IgnoreAntiforgeryTokenAttribute>().Any())
            return;

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            var pd = new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = ErrorCodes.CsrfFailed };
            pd.Extensions["code"] = ErrorCodes.CsrfFailed;
            context.Result = new BadRequestObjectResult(pd);
        }
    }
}
