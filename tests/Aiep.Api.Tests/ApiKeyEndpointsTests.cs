using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>API key endpoints (ADR-0008, ADR-0015): create (secret once), list, revoke, and scope limits.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiKeyEndpointsTests(ApiFixture api)
{
    [Fact]
    public async Task An_owner_creates_a_working_key_and_sees_it_in_the_list()
    {
        var (alice, org) = await NewOrgAsync();

        var created = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", alice,
            new { name = "ci", permissions = new[] { "runs:read", "runs:submit" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<CreatedApiKeyResponse>())!;
        Assert.StartsWith("aiep_test_", body.Secret);
        Assert.StartsWith("key_", body.Key.Id);

        // The returned secret really authenticates.
        var me = await SendAsync(HttpMethod.Get, "/v1/me", body.Secret);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // It is listed, metadata only — the secret is never returned again.
        var list = await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/api-keys", alice);
        var raw = await list.Content.ReadAsStringAsync();
        Assert.Contains(body.Key.Id, raw);
        Assert.DoesNotContain(body.Secret, raw);
    }

    [Fact]
    public async Task Revoking_a_key_stops_it_working_and_is_idempotent()
    {
        var (alice, org) = await NewOrgAsync();
        var created = (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", alice,
            new { name = "temp", permissions = new[] { "runs:read" } })).Content.ReadFromJsonAsync<CreatedApiKeyResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/me", created.Secret)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys/{created.Key.Id}/revoke", alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", created.Secret)).StatusCode);

        // Revoking again is a no-op, not an error.
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys/{created.Key.Id}/revoke", alice)).StatusCode);
    }

    [Fact]
    public async Task Revoking_an_unknown_key_is_404()
    {
        var (alice, org) = await NewOrgAsync();
        var ghost = ResourceId.Format(ResourceId.ApiKey, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys/{ghost}/revoke", alice)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_permission_is_400()
    {
        var (alice, org) = await NewOrgAsync();

        var response = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", alice,
            new { name = "bad", permissions = new[] { "runs:teleport" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_create_or_list_keys()
    {
        var (alice, org) = await NewOrgAsync();
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", bob, new { name = "x", permissions = new[] { "runs:read" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/api-keys", bob)).StatusCode);
    }

    [Fact]
    public async Task A_developer_can_create_a_scoped_key_but_not_exceed_their_permissions()
    {
        var (alice, org) = await NewOrgAsync();
        var ws = (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "dev", name = "Dev" }))
            .Content.ReadFromJsonAsync<WorkspaceResponse>())!;
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });
        await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{bobId}", alice, new { role = "Developer" });

        // Within a Developer's permissions: allowed.
        var ok = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", bob,
            new { name = "dev-key", workspaceId = ws.Id, permissions = new[] { "runs:submit", "definitions:write" } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        // Beyond them: forbidden (a Developer has no grants:manage).
        var denied = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", bob,
            new { name = "too-much", workspaceId = ws.Id, permissions = new[] { "grants:manage" } });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task A_non_member_cannot_create_a_key()
    {
        var (_, org) = await NewOrgAsync();
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/api-keys", bob, new { name = "x", permissions = new[] { "runs:read" } })).StatusCode);
    }

    private async Task<(string Token, string Org)> NewOrgAsync()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Keys Co" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        return (alice, org.Id);
    }

    private async Task<Guid> SubjectOfAsync(string bearer)
    {
        var me = (await (await SendAsync(HttpMethod.Get, "/v1/me", bearer)).Content.ReadFromJsonAsync<MeResponse>())!;
        ResourceId.TryParse(ResourceId.Subject, me.SubjectId, out var subjectId);
        return subjectId;
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
