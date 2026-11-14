using Aiep.Application.Identity;

namespace Aiep.Api.Http;

/// <summary>
/// Refuses requests that carry credentials in the URL (ADR-0008: keys only in the Authorization header). URLs end up
/// in proxy logs, browser history and referrers, so the request is rejected before authentication, and the response
/// tells the caller to rotate the key. The URL itself is never logged here.
/// </summary>
public sealed class CredentialsInUrlMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> CredentialParameters =
        new(["access_token", "id_token", "api_key", "apikey", "key", "token"], StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
    {
        if (CarriesCredentials(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Credentials are not accepted in the URL.",
                    Detail = "Send credentials only in the Authorization header. Treat any key sent in a URL as leaked and revoke it.",
                    Extensions = { ["code"] = "auth.credentials_in_url" },
                },
            });
            return;
        }

        await next(context);
    }

    private static bool CarriesCredentials(HttpRequest request)
    {
        if (!request.QueryString.HasValue)
        {
            return false;
        }

        foreach (var (name, values) in request.Query)
        {
            if (CredentialParameters.Contains(name) || ApiKeyFormat.ContainsKeyLikeValue(name))
            {
                return true;
            }

            foreach (var value in values)
            {
                if (ApiKeyFormat.ContainsKeyLikeValue(value))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
