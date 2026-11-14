namespace Aiep.Api.Authentication;

/// <summary>OIDC token validation (ADR-0008, ADR-0016). Bound from <c>Authentication:Oidc</c>.</summary>
public sealed class OidcSettings
{
    public const string Section = "Authentication:Oidc";

    /// <summary>Expected <c>iss</c>, for example <c>http://localhost:8081/realms/aiep</c>.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Expected <c>aud</c>.</summary>
    public string Audience { get; set; } = "aiep-api";

    /// <summary>
    /// Where to fetch discovery and keys, when the API reaches the IdP by a different address than the issuer
    /// (Compose: <c>http://keycloak:8080/...</c> while tokens say <c>http://localhost:8081/...</c>).
    /// Defaults to <c>{Issuer}/.well-known/openid-configuration</c>.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>May be false only in Development (ADR-0016).</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>API key verification (ADR-0008). Bound from <c>Authentication:ApiKeys</c>.</summary>
public sealed class ApiKeySettings
{
    public const string Section = "Authentication:ApiKeys";

    /// <summary>This deployment's environment tag; keys minted for any other environment are rejected.</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>Base64 HMAC pepper, at least 32 bytes. From user secrets locally, Key Vault in Azure.</summary>
    public string Pepper { get; set; } = string.Empty;

    internal byte[]? TryDecodePepper()
    {
        try
        {
            return Convert.FromBase64String(Pepper);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
