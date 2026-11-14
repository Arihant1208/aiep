using Microsoft.AspNetCore.Mvc;

namespace Aiep.Api.Http;

/// <summary>
/// A Problem Details result written through <see cref="IProblemDetailsService"/>, so every failure gets the stable
/// <c>code</c> and the <c>correlationId</c> that <see cref="ProblemCodes"/> adds (ADR-0015). <see cref="Results"/>
/// .Problem serializes directly and would skip that customization, so endpoints use these factories instead.
/// </summary>
internal sealed class CodedProblem(int status, string? code, string? detail, string? title) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = status;
        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        await service.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}

/// <summary>Problem Details results for endpoints. A null <c>code</c> lets <see cref="ProblemCodes"/> fill the default by status.</summary>
internal static class Problems
{
    public static IResult NotFound(string? detail = null) =>
        new CodedProblem(StatusCodes.Status404NotFound, code: null, detail, title: null);

    public static IResult Forbidden(string? detail = null) =>
        new CodedProblem(StatusCodes.Status403Forbidden, code: null, detail, title: null);

    public static IResult BadRequest(string detail, string code = "request.invalid") =>
        new CodedProblem(StatusCodes.Status400BadRequest, code, detail, title: null);

    public static IResult Conflict(string code, string detail) =>
        new CodedProblem(StatusCodes.Status409Conflict, code, detail, title: null);
}
