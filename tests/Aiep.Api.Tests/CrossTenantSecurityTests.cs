using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Identity;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>
/// The Phase 1 headline deliverable (Spec §6.5): tenant isolation proven end to end. Two organizations A and B; as A,
/// by OIDC token and by API key, every one of B's resources is read, mutated, enumerated and guessed by id through every
/// endpoint — and always gets 404, and B is never changed. Plus role escalation, revoked/expired/wrong-environment keys,
/// and a removed member's next request.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CrossTenantSecurityTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    private sealed record Tenant(
        string Token, string OrgId, string WorkspaceId, string MemberSubjectId, string KeyPlaintext, string KeyId, string Name);

    [Fact]
    public async Task As_tenant_A_every_tenant_B_endpoint_is_404_by_token_and_by_key()
    {
        var a = await BuildTenantAsync("alice", "alice-local-dev", "Tenant A", "a-extra");
        var b = await BuildTenantAsync("bob", "bob-local-dev", "Tenant B", "b-extra");
        var ghost = ResourceId.Format(ResourceId.Subject, await Seed.SubjectAsync(_tdb, "ghost"));

        // Every endpoint, aimed at B using B's real ids (so this is also id-guessing), run as A.
        (HttpMethod Method, string Path, object? Body)[] endpoints =
        [
            (HttpMethod.Get, $"/v1/organizations/{b.OrgId}", null),
            (HttpMethod.Get, $"/v1/me/permissions?organization={b.OrgId}", null),
            (HttpMethod.Get, $"/v1/organizations/{b.OrgId}/members", null),
            (HttpMethod.Post, $"/v1/organizations/{b.OrgId}/members", new { subjectId = ghost, role = "Member" }),
            (HttpMethod.Put, $"/v1/organizations/{b.OrgId}/members/{b.MemberSubjectId}", new { role = "Admin" }),
            (HttpMethod.Delete, $"/v1/organizations/{b.OrgId}/members/{b.MemberSubjectId}", null),
            (HttpMethod.Get, $"/v1/organizations/{b.OrgId}/workspaces", null),
            (HttpMethod.Post, $"/v1/organizations/{b.OrgId}/workspaces", new { slug = "intruder", name = "Intruder" }),
            (HttpMethod.Get, $"/v1/organizations/{b.OrgId}/workspaces/{b.WorkspaceId}", null),
            (HttpMethod.Post, $"/v1/organizations/{b.OrgId}/workspaces/{b.WorkspaceId}/archive", null),
            (HttpMethod.Put, $"/v1/organizations/{b.OrgId}/workspaces/{b.WorkspaceId}/grants/{b.MemberSubjectId}", new { role = "WorkspaceAdmin" }),
            (HttpMethod.Delete, $"/v1/organizations/{b.OrgId}/workspaces/{b.WorkspaceId}/grants/{b.MemberSubjectId}", null),
            (HttpMethod.Get, $"/v1/organizations/{b.OrgId}/api-keys", null),
            (HttpMethod.Post, $"/v1/organizations/{b.OrgId}/api-keys", new { name = "intruder", permissions = new[] { "runs:read" } }),
            (HttpMethod.Post, $"/v1/organizations/{b.OrgId}/api-keys/{b.KeyId}/revoke", null),
        ];

        var failures = new List<string>();
        foreach (var (method, path, body) in endpoints)
        {
            foreach (var (who, bearer) in new[] { ("token", a.Token), ("key", a.KeyPlaintext) })
            {
                var response = await SendAsync(method, path, bearer, body);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    failures.Add($"{method} {path} as A ({who}) → {(int)response.StatusCode}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Cross-tenant access was not concealed:\n" + string.Join("\n", failures));

        // B is intact: nothing A attempted changed anything.
        Assert.Equal(b.Name, (await ReadAsync<OrganizationResponse>(HttpMethod.Get, $"/v1/organizations/{b.OrgId}", b.Token)).Name);
        Assert.Equal("active", (await ReadAsync<WorkspaceResponse>(HttpMethod.Get, $"/v1/organizations/{b.OrgId}/workspaces/{b.WorkspaceId}", b.Token)).Status);
        Assert.Equal(2, (await ReadAsync<List<MemberResponse>>(HttpMethod.Get, $"/v1/organizations/{b.OrgId}/members", b.Token)).Count);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/me", b.KeyPlaintext)).StatusCode);
    }

    [Fact]
    public async Task An_api_key_cannot_exceed_its_scope_within_its_own_organization()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", alice, new { name = "Scoped Co" }));
        var key = (await CreateKeyAsync(alice, org.Id, "runs:read")).Secret;
        var ghost = ResourceId.Format(ResourceId.Subject, await Seed.SubjectAsync(_tdb, "scoped-ghost"));

        // The key's owner is the Owner, but the key is scoped to runs:read only.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org.Id}/members", key, new { subjectId = ghost, role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org.Id}/api-keys", key)).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_escalate_their_own_role()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var org = await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", alice, new { name = "Escalation Co" });
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org.Id}/members", alice, new { subjectId = bobId, role = "Member" });

        // A plain Member has no members:manage, so cannot promote anyone — including themselves.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Put, $"/v1/organizations/{org.Id}/members/{bobId}", bob, new { role = "Owner" })).StatusCode);
    }

    [Fact]
    public async Task A_removed_member_loses_access_on_their_next_request()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var org = await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", alice, new { name = "Revolving Co" });
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org.Id}/members", alice, new { subjectId = bobId, role = "Admin" });

        // As an Admin, Bob can read the organization.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org.Id}", bob)).StatusCode);

        await SendAsync(HttpMethod.Delete, $"/v1/organizations/{org.Id}/members/{bobId}", alice);

        // His valid token now resolves to no membership: the organization is concealed.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org.Id}", bob)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_cannot_act()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", alice, new { name = "Revoke Co" });
        var created = await ReadAsync<CreatedApiKeyResponse>(HttpMethod.Post, $"/v1/organizations/{org.Id}/api-keys", alice,
            new { name = "doomed", permissions = new[] { "org:read" } });

        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org.Id}/api-keys/{created.Key.Id}/revoke", alice);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", created.Secret)).StatusCode);
    }

    [Fact]
    public async Task An_expired_key_cannot_act()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", alice, new { name = "Expiry Co" });
        var created = await ReadAsync<CreatedApiKeyResponse>(HttpMethod.Post, $"/v1/organizations/{org.Id}/api-keys", alice,
            new { name = "stale", permissions = new[] { "org:read" }, expiresAt = DateTimeOffset.UtcNow.AddHours(-1) });

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", created.Secret)).StatusCode);
    }

    [Fact]
    public async Task A_key_from_another_environment_is_rejected_here()
    {
        // The API runs as environment "test"; a "prod" key (as if copied from another deployment) must never work.
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", alice, new { name = "Env Co" });
        ResourceId.TryParse(ResourceId.Organization, org.Id, out var organizationId);
        var owner = await SubjectOfAsync(alice);
        var prodKey = ApiKeyFormat.Generate("prod");
        await _tdb.RunAsync("aiep_api", organizationId, owner, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.api_keys
                (id, organization_id, workspace_id, key_id, secret_hash, environment, name, owner_subject_id, permissions)
            VALUES ($1, $2, NULL, $3, $4, 'prod', 'foreign', $5, ARRAY['org:read'])
            """,
            Guid.CreateVersion7(), organizationId, prodKey.KeyId,
            new ApiKeyHasher(ApiFixture.Pepper).Hash(prodKey.Secret), owner));

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", prodKey.Plaintext)).StatusCode);
    }

    private async Task<Tenant> BuildTenantAsync(string user, string password, string name, string extraMember)
    {
        var token = await api.GetTokenAsync(user, password);
        var org = await ReadAsync<OrganizationResponse>(HttpMethod.Post, "/v1/organizations", token, new { name });
        var ws = await ReadAsync<WorkspaceResponse>(HttpMethod.Post, $"/v1/organizations/{org.Id}/workspaces", token, new { slug = "main", name = "Main" });
        var memberId = ResourceId.Format(ResourceId.Subject, await Seed.SubjectAsync(_tdb, extraMember));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org.Id}/members", token, new { subjectId = memberId, role = "Member" });
        var key = await CreateKeyAsync(token, org.Id,
            "org:read", "members:read", "members:manage", "workspaces:create", "workspaces:manage", "grants:manage",
            "keys:read", "keys:create", "keys:revoke");
        return new Tenant(token, org.Id, ws.Id, memberId, key.Secret, key.Key.Id, name);
    }

    private async Task<CreatedApiKeyResponse> CreateKeyAsync(string token, string orgId, params string[] permissions) =>
        await ReadAsync<CreatedApiKeyResponse>(HttpMethod.Post, $"/v1/organizations/{orgId}/api-keys", token,
            new { name = "ci", permissions });

    private async Task<Guid> SubjectOfAsync(string bearer)
    {
        var me = await ReadAsync<MeResponse>(HttpMethod.Get, "/v1/me", bearer);
        ResourceId.TryParse(ResourceId.Subject, me.SubjectId, out var subjectId);
        return subjectId;
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string path, string? bearer, object? body = null)
    {
        var response = await SendAsync(method, path, bearer, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
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
