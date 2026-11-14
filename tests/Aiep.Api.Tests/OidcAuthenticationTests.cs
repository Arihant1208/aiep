using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Aiep.Api.Authentication;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aiep.Api.Tests;

/// <summary>OIDC bearer tokens from a real Keycloak (ADR-0008, ADR-0016).</summary>
[Collection(ApiCollection.Name)]
public sealed class OidcAuthenticationTests(ApiFixture api)
{
    private async Task<HttpResponseMessage> GetAsync(string path, string? bearer)
    {
        using var client = api.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertUnauthenticatedProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("auth.unauthenticated", problem!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requests_without_credentials_get_401_problem_details()
    {
        await AssertUnauthenticatedProblemAsync(await GetAsync("/v1/me", bearer: null));
    }

    [Fact]
    public async Task A_valid_token_is_mapped_to_one_stable_subject()
    {
        var token = await api.GetTokenAsync("alice", "alice-local-dev");

        var first = await (await GetAsync("/v1/me", token)).Content.ReadFromJsonAsync<MeResponse>();
        var second = await (await GetAsync("/v1/me", await api.GetTokenAsync("alice", "alice-local-dev")))
            .Content.ReadFromJsonAsync<MeResponse>();

        Assert.Equal(AiepClaims.MethodOidc, first!.AuthenticationMethod);
        Assert.Null(first.ApiKey);
        Assert.Equal(first.SubjectId, second!.SubjectId);
        Assert.True(ResourceId.TryParse(ResourceId.Subject, first.SubjectId, out var subjectId));

        // The subject row is keyed by (iss, sub) from the token.
        var sub = JwtParts.Payload(token)["sub"]!.GetValue<string>();
        var (issuer, external) = await new TenantDb(api.Database).RunAsync("aiep_api", null, subjectId, async c => (
            await TenantDb.ScalarAsync<string>(c, "SELECT issuer FROM aiep.subjects WHERE id = $1", subjectId),
            await TenantDb.ScalarAsync<string>(c, "SELECT external_subject FROM aiep.subjects WHERE id = $1", subjectId)));
        Assert.Equal(api.Issuer, issuer);
        Assert.Equal(sub, external);
    }

    [Fact]
    public async Task Different_people_get_different_subjects()
    {
        var alice = await (await GetAsync("/v1/me", await api.GetTokenAsync("alice", "alice-local-dev")))
            .Content.ReadFromJsonAsync<MeResponse>();
        var bob = await (await GetAsync("/v1/me", await api.GetTokenAsync("bob", "bob-local-dev")))
            .Content.ReadFromJsonAsync<MeResponse>();

        Assert.NotEqual(alice!.SubjectId, bob!.SubjectId);
    }

    [Fact]
    public async Task Keycloak_roles_and_groups_in_the_token_reach_nothing_in_aiep()
    {
        var token = await api.GetTokenAsync(ApiFixture.DecoyAdmin, ApiFixture.DecoyAdminPassword);

        // Precondition: the token really does carry the decoy role.
        var roles = JwtParts.Payload(token)["realm_access"]!["roles"]!.AsArray().Select(r => r!.GetValue<string>());
        Assert.Contains("admin", roles);

        var probe = await (await GetAsync("/__test/claims", token)).Content.ReadFromJsonAsync<ClaimsProbe>();

        Assert.Equal(new[] { AiepClaims.SubjectId, AiepClaims.AuthenticationMethod }.Order(), probe!.ClaimTypes.Order());
        Assert.False(probe.IsInAdminRole);
        Assert.False(probe.IsInOwnersRole);
    }

    [Fact]
    public async Task A_token_signed_by_an_unknown_key_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = api.Issuer,
            Audience = "aiep-api",
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "attacker" }, SecurityAlgorithms.RsaSha256),
        });

        await AssertUnauthenticatedProblemAsync(await GetAsync("/v1/me", forged));
    }

    [Fact]
    public async Task An_unsigned_token_is_rejected()
    {
        var payload = JwtParts.Payload(await api.GetTokenAsync("alice", "alice-local-dev"));
        var unsigned = $"{JwtParts.Base64Url("""{"alg":"none","typ":"JWT"}""")}.{JwtParts.Base64Url(payload)}.";

        await AssertUnauthenticatedProblemAsync(await GetAsync("/v1/me", unsigned));
    }

    [Fact]
    public async Task A_tampered_token_is_rejected()
    {
        var token = await api.GetTokenAsync("alice", "alice-local-dev");
        var parts = token.Split('.');
        var payload = JwtParts.Payload(token);
        payload["sub"] = Guid.NewGuid().ToString();

        await AssertUnauthenticatedProblemAsync(await GetAsync("/v1/me", $"{parts[0]}.{JwtParts.Base64Url(payload)}.{parts[2]}"));
    }

    [Fact]
    public async Task A_token_without_the_api_audience_is_rejected()
    {
        var token = await api.GetTokenAsync("alice", "alice-local-dev", ApiFixture.NoAudienceClient);
        Assert.DoesNotContain("aiep-api", JwtParts.Payload(token)["aud"]?.ToJsonString() ?? string.Empty);

        await AssertUnauthenticatedProblemAsync(await GetAsync("/v1/me", token));
    }

    [Fact]
    public async Task A_token_from_another_realm_is_rejected()
    {
        await AssertUnauthenticatedProblemAsync(await GetAsync("/v1/me", await api.GetMasterRealmTokenAsync()));
    }

    [Fact]
    public async Task Health_endpoints_stay_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/health/ready", bearer: null)).StatusCode);
    }
}
