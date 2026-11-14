using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Identity;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Organization endpoints (ADR-0015): create, read, 404 concealment, 403, and audit.</summary>
[Collection(ApiCollection.Name)]
public sealed class OrganizationEndpointsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    [Fact]
    public async Task Create_returns_201_and_the_owner_can_read_it()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");

        var created = await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<OrganizationResponse>())!;
        Assert.StartsWith("org_", body.Id);
        Assert.Equal("Acme", body.Name);
        Assert.Equal("active", body.Status);

        var read = await SendAsync(HttpMethod.Get, $"/v1/organizations/{body.Id}", alice);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(body.Id, (await read.Content.ReadFromJsonAsync<OrganizationResponse>())!.Id);
    }

    [Fact]
    public async Task A_non_member_cannot_see_the_organization()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Alice Co" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;

        var problem = await SendAsync(HttpMethod.Get, $"/v1/organizations/{org.Id}", bob);

        Assert.Equal(HttpStatusCode.NotFound, problem.StatusCode);
        Assert.Equal("resource.not_found", await CodeAsync(problem));
    }

    [Fact]
    public async Task A_member_without_org_read_gets_403()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Shared" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        ResourceId.TryParse(ResourceId.Organization, org.Id, out var orgId);

        // Bob signs in (creating his subject), then is added as a plain Member — which grants no org:read.
        var bobSubject = await SubjectOfAsync(bob);
        await _tdb.RunAsync("aiep_api", orgId, null, c => TenantDb.ExecuteAsync(c,
            "INSERT INTO aiep.memberships (id, organization_id, subject_id, org_role) VALUES ($1, $2, $3, 'Member')",
            Guid.CreateVersion7(), orgId, bobSubject));

        var problem = await SendAsync(HttpMethod.Get, $"/v1/organizations/{org.Id}", bob);

        Assert.Equal(HttpStatusCode.Forbidden, problem.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(problem));
    }

    [Fact]
    public async Task A_malformed_id_is_concealed_as_404()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, "/v1/organizations/not-a-real-id", alice)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_rejected(string name)
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");

        var problem = await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name });

        Assert.Equal(HttpStatusCode.BadRequest, problem.StatusCode);
        Assert.Equal("request.invalid", await CodeAsync(problem));
    }

    [Fact]
    public async Task An_api_key_cannot_create_an_organization()
    {
        var tenant = await Seed.TenantAsync(_tdb, "orgkey");
        var key = await IssueKeyAsync(tenant);

        var problem = await SendAsync(HttpMethod.Post, "/v1/organizations", key, new { name = "From a key" });

        Assert.Equal(HttpStatusCode.Forbidden, problem.StatusCode);
    }

    [Fact]
    public async Task Creating_an_organization_writes_one_audit_event()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var aliceSubject = await SubjectOfAsync(alice);
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Audited" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        ResourceId.TryParse(ResourceId.Organization, org.Id, out var orgId);

        var actors = await _tdb.RunAsync("aiep_api", orgId, aliceSubject, c => TenantDb.ColumnAsync<Guid>(c,
            "SELECT actor_subject_id FROM aiep.audit_events WHERE action = 'organization.created' AND outcome = 'success'"));

        Assert.Equal([aliceSubject], actors);
    }

    private async Task<string> IssueKeyAsync(Tenant tenant)
    {
        var key = ApiKeyFormat.Generate(ApiFixture.ApiKeyEnvironment);
        await _tdb.RunAsync("aiep_api", tenant.OrganizationId, tenant.OwnerId, c => TenantDb.ExecuteAsync(c,
            """
            INSERT INTO aiep.api_keys
                (id, organization_id, workspace_id, key_id, secret_hash, environment, name, owner_subject_id, permissions)
            VALUES ($1, $2, $3, $4, $5, $6, 'orgkey', $7, ARRAY['org:read'])
            """,
            Guid.CreateVersion7(), tenant.OrganizationId, tenant.WorkspaceId, key.KeyId,
            new ApiKeyHasher(ApiFixture.Pepper).Hash(key.Secret), ApiFixture.ApiKeyEnvironment, tenant.OwnerId));
        return key.Plaintext;
    }

    private async Task<Guid> SubjectOfAsync(string bearer)
    {
        var me = (await (await SendAsync(HttpMethod.Get, "/v1/me", bearer)).Content.ReadFromJsonAsync<MeResponse>())!;
        ResourceId.TryParse(ResourceId.Subject, me.SubjectId, out var subjectId);
        return subjectId;
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["code"]!.GetValue<string>();
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
