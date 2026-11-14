namespace Aiep.Api.Authentication;

public static class AuthenticationSchemes
{
    /// <summary>Default policy scheme: forwards to <see cref="ApiKey"/> or <see cref="Oidc"/> by token shape.</summary>
    public const string Default = "Aiep";
    public const string Oidc = "Oidc";
    public const string ApiKey = "ApiKey";

    private const string BearerPrefix = "Bearer ";

    public static bool TryGetBearerToken(string? header, out string token)
    {
        token = string.Empty;
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[BearerPrefix.Length..].Trim();
        return token.Length > 0;
    }

    internal static string Select(HttpContext context)
    {
        var headers = context.Request.Headers.Authorization;
        return headers.Count == 1
               && TryGetBearerToken(headers.ToString(), out var token)
               && token.StartsWith(Application.Identity.ApiKeyFormat.Prefix, StringComparison.Ordinal)
            ? ApiKey
            : Oidc;
    }
}
