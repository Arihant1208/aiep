using System.Diagnostics;
using System.Text.Encodings.Web;
using Aiep.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aiep.Api.Authentication;

/// <summary>
/// Authenticates <c>Authorization: Bearer aiep_...</c> (ADR-0008). Every failure is the same 401 to the caller;
/// the reason goes to logs and the trace as a low-cardinality tag. The key itself is never logged.
/// </summary>
internal sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (headers.Count > 1 || !AuthenticationSchemes.TryGetBearerToken(headers.ToString(), out var token))
        {
            return AuthenticateResult.Fail("Invalid authorization header.");
        }

        var result = await authenticator.AuthenticateAsync(token, Context.RequestAborted);
        if (result.Failure is { } failure)
        {
            Activity.Current?.SetTag("aiep.auth.failure", failure.ToString());
            LogRejected(Logger, failure);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var principal = AiepClaims.ForApiKey(result.Key!, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "API key rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, ApiKeyFailure reason);
}
