using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aiep.Api.Authentication;
using Aiep.Api.Endpoints;
using Aiep.Application.Identity;
using Aiep.Application.Ids;
using Npgsql;

namespace Aiep.Api.Tests;

/// <summary>API keys over HTTP (ADR-0008), against the real database.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiKeyAuthenticationTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    private sealed record IssuedKey(string Plaintext, Guid Id, Tenant Tenant);

    private async Task<IssuedKey> IssueKeyAsync(
        string environment = ApiFixture.ApiKeyEnvironment, DateTimeOffset? expiresAt = null)
    {
        var tenant = await Seed.TenantAsync(_tdb, "keys");
        var key = ApiKeyFormat.Generate(environment);
        var id = Guid.CreateVersion7();
        await _tdb.RunAsync("aiep_api", tenant.OrganizationId, tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.api_keys
                (id, organization_id, workspace_id, key_id, secret_hash, environment, name, owner_subject_id, permissions, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, 'integration', $7, ARRAY['runs:read', 'runs:submit'], $8)
            """,
            id, tenant.OrganizationId, tenant.WorkspaceId, key.KeyId, new ApiKeyHasher(ApiFixture.Pepper).Hash(key.Secret),
            environment, tenant.OwnerId, expiresAt));
        return new IssuedKey(key.Plaintext, id, tenant);
    }

    private Task ExecuteAsync(Tenant tenant, string sql, params object?[] args) =>
        _tdb.RunAsync("aiep_api", tenant.OrganizationId, tenant.OwnerId, c => TenantDb.ExecuteAsync(c, sql, args));

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

    private static async Task<JsonObject> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task A_valid_key_authenticates_as_its_owner_with_its_scope()
    {
        var key = await IssueKeyAsync();

        var response = await GetAsync("/v1/me", key.Plaintext);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(AiepClaims.MethodApiKey, me!.AuthenticationMethod);
        Assert.Equal(ResourceId.Format(ResourceId.Subject, key.Tenant.OwnerId), me.SubjectId);
        Assert.Equal(ResourceId.Format(ResourceId.ApiKey, key.Id), me.ApiKey!.Id);
        Assert.Equal(ResourceId.Format(ResourceId.Organization, key.Tenant.OrganizationId), me.ApiKey.OrganizationId);
        Assert.Equal(ResourceId.Format(ResourceId.Workspace, key.Tenant.WorkspaceId), me.ApiKey.WorkspaceId);
        Assert.Equal(["runs:read", "runs:submit"], me.ApiKey.Permissions);
    }

    public static TheoryData<string> RejectionCases() =>
        ["wrong-secret", "unknown-key", "revoked", "expired", "other-environment", "malformed", "owner-removed", "owner-suspended"];

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public async Task Rejected_keys_all_get_the_same_401(string rejection)
    {
        var key = await IssueKeyAsync(
            environment: rejection == "other-environment" ? "prod" : ApiFixture.ApiKeyEnvironment,
            expiresAt: rejection == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : null);
        var presented = key.Plaintext;

        switch (rejection)
        {
            case "wrong-secret":
                presented = presented[..^4] + (presented[^4..] == "0000" ? "1111" : "0000");
                break;
            case "unknown-key":
                ApiKeyFormat.TryParse(presented, out var parts);
                presented = ApiKeyFormat.Generate(ApiFixture.ApiKeyEnvironment).Plaintext.Replace(
                    ApiKeyFormat.Generate(ApiFixture.ApiKeyEnvironment).Secret, parts.Secret, StringComparison.Ordinal);
                break;
            case "revoked":
                await ExecuteAsync(key.Tenant, "UPDATE aiep.api_keys SET revoked_at = now() WHERE id = $1", key.Id);
                break;
            case "malformed":
                presented = "aiep_test_not-a-real-key";
                break;
            case "owner-removed":
                await ExecuteAsync(key.Tenant, "DELETE FROM aiep.memberships WHERE subject_id = $1", key.Tenant.OwnerId);
                break;
            case "owner-suspended":
                await ExecuteAsync(key.Tenant, "UPDATE aiep.memberships SET status = 'suspended' WHERE subject_id = $1", key.Tenant.OwnerId);
                break;
        }

        var problem = await ProblemAsync(await GetAsync("/v1/me", presented), HttpStatusCode.Unauthorized);

        // Same code and title whatever the reason: the response must not help an attacker narrow anything down.
        Assert.Equal("auth.unauthenticated", problem["code"]!.GetValue<string>());
        Assert.Equal("Unauthorized", problem["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_removed_owner_loses_access_on_the_next_request()
    {
        var key = await IssueKeyAsync();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/v1/me", key.Plaintext)).StatusCode);

        await ExecuteAsync(key.Tenant, "DELETE FROM aiep.memberships WHERE subject_id = $1", key.Tenant.OwnerId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("/v1/me", key.Plaintext)).StatusCode);
    }

    [Fact]
    public async Task Use_is_recorded_at_most_once_a_minute()
    {
        var key = await IssueKeyAsync();

        async Task<DateTimeOffset?> LastUsedAsync() => await _tdb.RunAsync("aiep_api", key.Tenant.OrganizationId, key.Tenant.OwnerId,
            async c =>
            {
                await using var command = new NpgsqlCommand("SELECT last_used_at FROM aiep.api_keys WHERE id = $1", c);
                command.Parameters.Add(new NpgsqlParameter { Value = key.Id });
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                return reader.IsDBNull(0) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(0);
            });

        Assert.Null(await LastUsedAsync());
        await GetAsync("/v1/me", key.Plaintext);
        var first = await LastUsedAsync();
        await GetAsync("/v1/me", key.Plaintext);
        var second = await LastUsedAsync();

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("/v1/me?api_key={key}")]
    [InlineData("/v1/me?q={key}")]
    [InlineData("/v1/me?access_token=whatever")]
    public async Task Credentials_in_the_url_are_refused_even_with_a_valid_header(string pathTemplate)
    {
        var key = await IssueKeyAsync();

        var response = await GetAsync(pathTemplate.Replace("{key}", Uri.EscapeDataString(key.Plaintext), StringComparison.Ordinal), key.Plaintext);

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("auth.credentials_in_url", problem["code"]!.GetValue<string>());
        Assert.NotNull(problem["correlationId"]);
    }

    [Fact]
    public async Task Authenticated_unknown_routes_return_404_problem_details()
    {
        var key = await IssueKeyAsync();

        var problem = await ProblemAsync(await GetAsync("/v1/does-not-exist", key.Plaintext), HttpStatusCode.NotFound);

        Assert.Equal("resource.not_found", problem["code"]!.GetValue<string>());
    }
}
