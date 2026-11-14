using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Task-definition and version endpoints (ADR-0007, ADR-0015): authoring, publish, the handler allowlist, and
/// workspace-scoped authorization/concealment.</summary>
[Collection(ApiCollection.Name)]
public sealed class TaskDefinitionEndpointsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    private static object ValidVersion => new
    {
        inputSchema = new { type = "object" },
        outputSchema = new { type = "object" },
        handlerType = "completion",
        handlerVersion = "1",
        modelProvider = "fake",
        modelName = "fake-1",
    };

    [Fact]
    public async Task Owner_can_create_a_definition_publish_a_version_and_read_it_back()
    {
        var (alice, org) = await NewOrgAsync("Catalog Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var basePath = $"/v1/organizations/{org}/workspaces/{ws}/task-definitions";

        var created = await SendAsync(HttpMethod.Post, basePath, alice, new { slug = "summarize", name = "Summarize" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var definition = (await created.Content.ReadFromJsonAsync<TaskDefinitionResponse>())!;
        Assert.StartsWith("def_", definition.Id);
        Assert.Null(definition.CurrentVersion);

        var versioned = await SendAsync(HttpMethod.Post, $"{basePath}/{definition.Id}/versions", alice, ValidVersion);
        Assert.Equal(HttpStatusCode.Created, versioned.StatusCode);
        var version = (await versioned.Content.ReadFromJsonAsync<TaskDefinitionVersionResponse>())!;
        Assert.Equal(1, version.Version);
        Assert.Equal("draft", version.Status);

        var published = await SendAsync(HttpMethod.Post, $"{basePath}/{definition.Id}/versions/1/publish", alice);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal("published", (await published.Content.ReadFromJsonAsync<TaskDefinitionVersionResponse>())!.Status);

        var fetched = (await (await SendAsync(HttpMethod.Get, $"{basePath}/{definition.Id}", alice))
            .Content.ReadFromJsonAsync<TaskDefinitionResponse>())!;
        Assert.Equal(1, fetched.CurrentVersion);

        // Publishing again is idempotent.
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Post, $"{basePath}/{definition.Id}/versions/1/publish", alice)).StatusCode);
    }

    [Fact]
    public async Task A_version_with_a_non_allowlisted_handler_is_rejected()
    {
        var (alice, org) = await NewOrgAsync("Allowlist Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var basePath = $"/v1/organizations/{org}/workspaces/{ws}/task-definitions";
        var def = (await (await SendAsync(HttpMethod.Post, basePath, alice, new { slug = "x", name = "X" }))
            .Content.ReadFromJsonAsync<TaskDefinitionResponse>())!;

        var response = await SendAsync(HttpMethod.Post, $"{basePath}/{def.Id}/versions", alice, new
        {
            inputSchema = new { type = "object" },
            outputSchema = new { type = "object" },
            handlerType = "not-a-real-handler",
            handlerVersion = "1",
            modelProvider = "fake",
            modelName = "fake-1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_read_but_not_author_definitions()
    {
        var (alice, org) = await NewOrgAsync("Viewer Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var basePath = $"/v1/organizations/{org}/workspaces/{ws}/task-definitions";
        await SendAsync(HttpMethod.Post, basePath, alice, new { slug = "seed", name = "Seed" });

        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobSubject = await SubjectOfAsync(bob);
        await AddMemberAsync(org, bobSubject, "Member");
        await SendAsync(HttpMethod.Put,
            $"/v1/organizations/{org}/workspaces/{ws}/grants/{ResourceId.Format(ResourceId.Subject, bobSubject)}",
            alice, new { role = "Viewer" });

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, basePath, bob)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, basePath, bob, new { slug = "nope", name = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task A_non_member_sees_only_404()
    {
        var (alice, org) = await NewOrgAsync("Closed Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var basePath = $"/v1/organizations/{org}/workspaces/{ws}/task-definitions";
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, basePath, bob)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, basePath, bob, new { slug = "x", name = "X" })).StatusCode);
    }

    [Fact]
    public async Task An_unknown_definition_is_404()
    {
        var (alice, org) = await NewOrgAsync("Missing Co");
        var ws = await CreateWorkspaceAsync(alice, org, "main");
        var strangerId = ResourceId.Format(ResourceId.TaskDefinition, Guid.CreateVersion7());

        var response = await SendAsync(HttpMethod.Get,
            $"/v1/organizations/{org}/workspaces/{ws}/task-definitions/{strangerId}", alice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string Token, string Org)> NewOrgAsync(string name)
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        return (alice, org.Id);
    }

    private async Task<string> CreateWorkspaceAsync(string token, string org, string slug) =>
        (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", token, new { slug, name = slug }))
            .Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;

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
