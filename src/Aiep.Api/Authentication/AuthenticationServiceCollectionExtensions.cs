using Aiep.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aiep.Api.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    // RS256 is Keycloak's default; ES256 allowed for a later key change. Never "none" or HMAC algorithms.
    private static readonly string[] AllowedAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256];

    public static IServiceCollection AddAiepAuthentication(this IServiceCollection services)
    {
        services.AddOptions<OidcSettings>()
            .BindConfiguration(OidcSettings.Section)
            .Validate(o => Uri.TryCreate(o.Issuer, UriKind.Absolute, out _), "Authentication:Oidc:Issuer must be an absolute URL.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "Authentication:Oidc:Audience is required.")
            .Validate<IHostEnvironment>(
                (o, env) => o.RequireHttpsMetadata || env.IsDevelopment(),
                "Authentication:Oidc:RequireHttpsMetadata may be false only in Development.")
            .ValidateOnStart();

        services.AddOptions<ApiKeySettings>()
            .BindConfiguration(ApiKeySettings.Section)
            .Validate(o => ApiKeyFormat.IsValidEnvironment(o.Environment),
                "Authentication:ApiKeys:Environment must be 2-10 lowercase letters.")
            .Validate(o => o.TryDecodePepper() is { Length: >= ApiKeyHasher.MinimumPepperBytes },
                $"Authentication:ApiKeys:Pepper must be base64 of at least {ApiKeyHasher.MinimumPepperBytes} bytes.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ApiKeyHasher(
            sp.GetRequiredService<IOptions<ApiKeySettings>>().Value.TryDecodePepper()!));
        services.AddSingleton(sp => new ApiKeyAuthenticator(
            sp.GetRequiredService<IApiKeyStore>(),
            sp.GetRequiredService<ApiKeyHasher>(),
            sp.GetRequiredService<IOptions<ApiKeySettings>>().Value.Environment,
            sp.GetRequiredService<TimeProvider>()));

        services.AddAuthentication(AuthenticationSchemes.Default)
            .AddPolicyScheme(AuthenticationSchemes.Default, AuthenticationSchemes.Default,
                o => o.ForwardDefaultSelector = AuthenticationSchemes.Select)
            .AddJwtBearer(AuthenticationSchemes.Oidc, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthenticationSchemes.ApiKey, _ => { });

        // Configured from options at resolve time, so test and environment overrides apply.
        services.AddOptions<JwtBearerOptions>(AuthenticationSchemes.Oidc)
            .Configure<IOptions<OidcSettings>>((jwt, oidcOptions) => ConfigureJwt(jwt, oidcOptions.Value));

        // Every endpoint requires an authenticated caller unless it opts out (health checks do).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    private static void ConfigureJwt(JwtBearerOptions jwt, OidcSettings oidc)
    {
        jwt.MetadataAddress = oidc.MetadataAddress
            ?? $"{oidc.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        jwt.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = oidc.Issuer,
            ValidateAudience = true,
            ValidAudience = oidc.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = AllowedAlgorithms,
        };
        jwt.Events = new JwtBearerEvents { OnTokenValidated = ReplacePrincipalAsync };
    }

    /// <summary>
    /// After the token is validated: map <c>(iss, sub)</c> to the AIEP subject and replace the principal with one
    /// that carries only AIEP claims. Nothing else from the token survives.
    /// </summary>
    private static async Task ReplacePrincipalAsync(TokenValidatedContext context)
    {
        if (context.SecurityToken is not JsonWebToken token)
        {
            context.Fail("Unsupported token type.");
            return;
        }

        token.TryGetPayloadValue<string>("name", out var name);
        token.TryGetPayloadValue<string>("preferred_username", out var username);
        token.TryGetPayloadValue<string>("email", out var email);
        var identity = ExternalIdentity.TryCreate(token.Issuer, token.Subject, name ?? username, email);
        if (identity is null)
        {
            context.Fail("Token has no usable subject.");
            return;
        }

        var directory = context.HttpContext.RequestServices.GetRequiredService<ISubjectDirectory>();
        var subjectId = await directory.ResolveAsync(identity, context.HttpContext.RequestAborted);
        context.Principal = AiepClaims.ForUser(subjectId, AuthenticationSchemes.Oidc);
    }
}
