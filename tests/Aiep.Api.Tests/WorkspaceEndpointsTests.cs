using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Workspace and grant endpoints (ADR-0010, ADR-0015): creation, concealment, archive, and delegation.</summary>
[Collection(ApiCollection.Name)]
public sealed class WorkspaceEndpointsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    [Fact]
    public async Task Owner_can_create_list_and_get_a_workspace()
    {
        var (alice, org) = await NewOrgAsync("Workspaces Co");

        var created = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "prod", name = "Production" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var workspace = (await created.Content.ReadFromJsonAsync<WorkspaceResponse>())!;
        Assert.StartsWith("ws_", workspace.Id);
        Assert.Equal("prod", workspace.Slug);
        Assert.Equal("active", workspace.Status);

        var list = await (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces", alice))
            .Content.ReadFromJsonAsync<List<WorkspaceResponse>>();
        Assert.Contains(list!, w => w.Id == workspace.Id);

        var get = await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces/{workspace.Id}", alice);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_slug_is_409()
    {
        var (alice, org) = await NewOrgAsync("Dup Co");
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "main", name = "Main" });

        var again = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "main", name = "Main 2" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("workspace.slug_taken", await CodeAsync(again));
    }

    [Fact]
    public async Task An_invalid_slug_is_400()
    {
        var (alice, org) = await NewOrgAsync("Bad Slug Co");

        var response = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "NOT valid", name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_non_member_cannot_create_a_workspace()
    {
        var (_, org) = await NewOrgAsync("Closed Co");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");

        var response = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", bob, new { slug = "x", name = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_member_without_create_permission_gets_403()
    {
        var (_, org) = await NewOrgAsync("Members Co");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        await AddMemberAsync(org, await SubjectOfAsync(bob), "Member");

        var response = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", bob, new { slug = "x", name = "X" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_can_archive_a_workspace()
    {
        var (alice, org) = await NewOrgAsync("Archive Co");
        var ws = await CreateWorkspaceAsync(alice, org, "temp");

        var archived = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces/{ws.Id}/archive", alice);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var body = (await archived.Content.ReadFromJsonAsync<WorkspaceResponse>())!;
        Assert.Equal("archived", body.Status);
        Assert.NotNull(body.ArchivedAt);
    }

    [Fact]
    public async Task A_grant_reveals_a_workspace_and_removing_it_conceals_it_again()
    {
        var (alice, org) = await NewOrgAsync("Grant Co");
        var ws = await CreateWorkspaceAsync(alice, org, "shared");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobSubject = await SubjectOfAsync(bob);
        await AddMemberAsync(org, bobSubject, "Member");

        // A member with no grant cannot see the workspace at all.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces/{ws.Id}", bob)).StatusCode);

        var bobId = ResourceId.Format(ResourceId.Subject, bobSubject);
        var granted = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{bobId}", alice, new { role = "Developer" });
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces/{ws.Id}", bob)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{bobId}", alice)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces/{ws.Id}", bob)).StatusCode);
    }

    [Fact]
    public async Task Granting_to_a_non_member_is_404()
    {
        var (alice, org) = await NewOrgAsync("Stranger Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var stranger = ResourceId.Format(ResourceId.Subject, await Seed.SubjectAsync(_tdb, "carol"));

        var response = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{stranger}", alice, new { role = "Viewer" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_developer_cannot_manage_grants()
    {
        var (alice, org) = await NewOrgAsync("NoDelegate Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobSubject = await SubjectOfAsync(bob);
        await AddMemberAsync(org, bobSubject, "Member");
        var bobId = ResourceId.Format(ResourceId.Subject, bobSubject);
        await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{bobId}", alice, new { role = "Developer" });

        // Bob, a Developer, lacks grants:manage.
        var response = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{bobId}", bob, new { role = "Viewer" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_list_shows_only_workspaces_the_caller_can_see()
    {
        var (alice, org) = await NewOrgAsync("Visibility Co");
        var open = await CreateWorkspaceAsync(alice, org, "open");
        await CreateWorkspaceAsync(alice, org, "secret");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobSubject = await SubjectOfAsync(bob);
        await AddMemberAsync(org, bobSubject, "Member");
        await SendAsync(HttpMethod.Put,
            $"/v1/organizations/{org}/workspaces/{open.Id}/grants/{ResourceId.Format(ResourceId.Subject, bobSubject)}", alice, new { role = "Viewer" });

        var aliceList = await (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces", alice))
            .Content.ReadFromJsonAsync<List<WorkspaceResponse>>();
        var bobList = await (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces", bob))
            .Content.ReadFromJsonAsync<List<WorkspaceResponse>>();

        Assert.Equal(2, aliceList!.Count); // owner sees all
        Assert.Equal([open.Id], bobList!.Select(w => w.Id)); // member sees only the granted one
    }

    private async Task<(string Token, string Org)> NewOrgAsync(string name)
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        return (alice, org.Id);
    }

    private async Task<WorkspaceResponse> CreateWorkspaceAsync(string token, string org, string slug) =>
        (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", token, new { slug, name = slug }))
            .Content.ReadFromJsonAsync<WorkspaceResponse>())!;

    private async Task AddMemberAsync(string org, Guid subject, string role)
    {
        ResourceId.TryParse(ResourceId.Organization, org, out var organizationId);
        await _tdb.RunAsync("aiep_api", organizationId, null, c => TenantDb.ExecuteAsync(c,
            "INSERT INTO aiep.memberships (id, organization_id, subject_id, org_role) VALUES ($1, $2, $3, $4)",
            Guid.CreateVersion7(), organizationId, subject, role));
    }

    private async Task<Guid> SubjectOfAsync(string bearer)
    {
        var me = (await (await SendAsync(HttpMethod.Get, "/v1/me", bearer)).Content.ReadFromJsonAsync<MeResponse>())!;
        ResourceId.TryParse(ResourceId.Subject, me.SubjectId, out var subjectId);
        return subjectId;
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["code"]!.GetValue<string>();

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
