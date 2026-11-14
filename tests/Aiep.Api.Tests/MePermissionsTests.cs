using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Identity;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary><c>GET /v1/me/permissions</c>: the ADR-0010 effective-permissions debugging aid.</summary>
[Collection(ApiCollection.Name)]
public sealed class MePermissionsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    [Fact]
    public async Task An_owner_sees_their_full_effective_permissions()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Perms Co" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;

        var response = await SendAsync(HttpMethod.Get, $"/v1/me/permissions?organization={org.Id}", alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = (await response.Content.ReadFromJsonAsync<MePermissionsResponse>())!;
        Assert.Equal("Owner", me.OrgRole);
        Assert.True(me.WorkspaceAdminEverywhere);
        Assert.Contains("org:manage", me.Permissions);
        Assert.Contains("org:transfer", me.Permissions);
    }

    [Fact]
    public async Task A_user_must_name_the_organization()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Get, "/v1/me/permissions", alice)).StatusCode);
    }

    [Fact]
    public async Task A_non_member_is_concealed()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Private" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/v1/me/permissions?organization={org.Id}", bob)).StatusCode);
    }

    [Fact]
    public async Task An_api_key_reports_its_own_organization_capped_by_its_scope()
    {
        var tenant = await Seed.TenantAsync(_tdb, "mekey");
        var key = await IssueKeyAsync(tenant, "runs:read");

        var me = (await (await SendAsync(HttpMethod.Get, "/v1/me/permissions", key))
            .Content.ReadFromJsonAsync<MePermissionsResponse>())!;

        Assert.Equal(ResourceId.Format(ResourceId.Organization, tenant.OrganizationId), me.OrganizationId);
        Assert.Equal("Owner", me.OrgRole);
        // Owner would have everything, but the key scope caps the effective set.
        Assert.Equal(["runs:read"], me.Permissions);
    }

    private async Task<string> IssueKeyAsync(Tenant tenant, params string[] permissions)
    {
        var key = ApiKeyFormat.Generate(ApiFixture.ApiKeyEnvironment);
        await _tdb.RunAsync("aiep_api", tenant.OrganizationId, tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.api_keys
                (id, organization_id, workspace_id, key_id, secret_hash, environment, name, owner_subject_id, permissions)
            VALUES ($1, $2, $3, $4, $5, $6, 'mekey', $7, $8)
            """,
            Guid.CreateVersion7(), tenant.OrganizationId, tenant.WorkspaceId, key.KeyId,
            new ApiKeyHasher(ApiFixture.Pepper).Hash(key.Secret), ApiFixture.ApiKeyEnvironment, tenant.OwnerId, permissions));
        return key.Plaintext;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, object? body = null)
    {
        using var client = api.Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}
