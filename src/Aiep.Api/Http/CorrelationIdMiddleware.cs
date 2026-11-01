using System.Diagnostics;

namespace Aiep.Api.Http;

/// <summary>
/// Assigns every request a correlation ID, echoes it in the response, and attaches it to logs and the
/// current trace. A client-supplied ID is accepted only if it is short and uses a safe character set.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("aiep.correlation_id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var supplied))
        {
            var value = supplied.ToString();
            if (IsAcceptable(value))
            {
                return value;
            }
        }

        return Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
    }

    private static bool IsAcceptable(string value) =>
        value.Length is > 0 and <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
