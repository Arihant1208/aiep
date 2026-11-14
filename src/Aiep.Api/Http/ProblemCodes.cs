namespace Aiep.Api.Http;

/// <summary>
/// Adds the stable machine-readable <c>code</c> and the <c>correlationId</c> to every Problem Details response
/// (ADR-0015). Endpoints that know a more specific code set it themselves; this fills in the default by status.
/// </summary>
public static class ProblemCodes
{
    public static void Apply(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        problem.Extensions.TryAdd("code", problem.Status switch
        {
            StatusCodes.Status400BadRequest => "request.invalid",
            StatusCodes.Status401Unauthorized => "auth.unauthenticated",
            StatusCodes.Status403Forbidden => "auth.forbidden",
            StatusCodes.Status404NotFound => "resource.not_found",
            StatusCodes.Status405MethodNotAllowed => "request.method_not_allowed",
            StatusCodes.Status409Conflict => "resource.conflict",
            StatusCodes.Status429TooManyRequests => "rate_limit.exceeded",
            _ => "server.error",
        });
    }
}
