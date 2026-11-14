using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Run submission and reads (ADR-0005, ADR-0015): acceptance, idempotency, unpublished guards, and cancellation.
/// Execution is proven separately; here the API host has no worker, so submitted runs stay Queued.</summary>
[Collection(ApiCollection.Name)]
public sealed class RunEndpointsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    [Fact]
    public async Task Submitting_a_run_accepts_it_as_queued_and_it_can_be_read_back()
    {
        var ctx = await NewWorkspaceAsync("Runs Co");
        var definition = await PublishedDefinitionAsync(ctx, "summarize");

        var submitted = await SendAsync(HttpMethod.Post, $"{ctx.Runs}", ctx.Token,
            new { taskDefinitionId = definition, input = new { q = "hi" } });

        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var run = (await submitted.Content.ReadFromJsonAsync<RunResponse>())!;
        Assert.StartsWith("run_", run.Id);
        Assert.Equal("Queued", run.Status);

        var fetched = await SendAsync(HttpMethod.Get, $"{ctx.Runs}/{run.Id}", ctx.Token);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("Queued", (await fetched.Content.ReadFromJsonAsync<RunResponse>())!.Status);
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_run_and_a_different_payload_conflicts()
    {
        var ctx = await NewWorkspaceAsync("Idem Co");
        var definition = await PublishedDefinitionAsync(ctx, "echo");
        var headers = new[] { ("Idempotency-Key", "key-123") };

        var first = await SendAsync(HttpMethod.Post, ctx.Runs, ctx.Token, new { taskDefinitionId = definition, input = new { a = 1 } }, headers);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstRun = (await first.Content.ReadFromJsonAsync<RunResponse>())!;

        var again = await SendAsync(HttpMethod.Post, ctx.Runs, ctx.Token, new { taskDefinitionId = definition, input = new { a = 1 } }, headers);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(firstRun.Id, (await again.Content.ReadFromJsonAsync<RunResponse>())!.Id);

        var conflict = await SendAsync(HttpMethod.Post, ctx.Runs, ctx.Token, new { taskDefinitionId = definition, input = new { a = 2 } }, headers);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency.conflict", await CodeAsync(conflict));
    }

    [Fact]
    public async Task Submitting_against_a_definition_with_no_published_version_is_409()
    {
        var ctx = await NewWorkspaceAsync("Draft Co");
        // A definition with only a draft version.
        var created = (await (await SendAsync(HttpMethod.Post, ctx.Definitions, ctx.Token, new { slug = "draft", name = "Draft" }))
            .Content.ReadFromJsonAsync<TaskDefinitionResponse>())!;
        await SendAsync(HttpMethod.Post, $"{ctx.Definitions}/{created.Id}/versions", ctx.Token, ValidVersion);

        var response = await SendAsync(HttpMethod.Post, ctx.Runs, ctx.Token, new { taskDefinitionId = created.Id, input = new { q = "x" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("definition.no_published_version", await CodeAsync(response));
    }

    [Fact]
    public async Task A_queued_run_can_be_cancelled_immediately()
    {
        var ctx = await NewWorkspaceAsync("Cancel Co");
        var definition = await PublishedDefinitionAsync(ctx, "cancelme");
        var run = (await (await SendAsync(HttpMethod.Post, ctx.Runs, ctx.Token, new { taskDefinitionId = definition, input = new { q = "x" } }))
            .Content.ReadFromJsonAsync<RunResponse>())!;

        var cancelled = await SendAsync(HttpMethod.Post, $"{ctx.Runs}/{run.Id}/cancel", ctx.Token);

        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<RunResponse>())!.Status);
    }

    [Fact]
    public async Task A_viewer_cannot_submit_runs()
    {
        var ctx = await NewWorkspaceAsync("Viewer Co");
        var definition = await PublishedDefinitionAsync(ctx, "task");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobSubject = await SubjectOfAsync(bob);
        await AddMemberAsync(ctx.Org, bobSubject, "Member");
        await SendAsync(HttpMethod.Put,
            $"/v1/organizations/{ctx.Org}/workspaces/{ctx.Ws}/grants/{ResourceId.Format(ResourceId.Subject, bobSubject)}",
            ctx.Token, new { role = "Viewer" });

        var response = await SendAsync(HttpMethod.Post, ctx.Runs, bob, new { taskDefinitionId = definition, input = new { q = "x" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static object ValidVersion => new
    {
        inputSchema = new { type = "object" },
        outputSchema = new { type = "object" },
        handlerType = "completion",
        handlerVersion = "1",
        modelProvider = "fake",
        modelName = "fake-1",
    };

    private sealed record WorkspaceContext(string Token, string Org, string Ws)
    {
        public string Definitions => $"/v1/organizations/{Org}/workspaces/{Ws}/task-definitions";

        public string Runs => $"/v1/organizations/{Org}/workspaces/{Ws}/runs";
    }

    private async Task<WorkspaceContext> NewWorkspaceAsync(string name)
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!.Id;
        var ws = (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "main", name = "Main" }))
            .Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
        return new WorkspaceContext(alice, org, ws);
    }

    private async Task<string> PublishedDefinitionAsync(WorkspaceContext ctx, string slug)
    {
        var definition = (await (await SendAsync(HttpMethod.Post, ctx.Definitions, ctx.Token, new { slug, name = slug }))
            .Content.ReadFromJsonAsync<TaskDefinitionResponse>())!;
        await SendAsync(HttpMethod.Post, $"{ctx.Definitions}/{definition.Id}/versions", ctx.Token, ValidVersion);
        await SendAsync(HttpMethod.Post, $"{ctx.Definitions}/{definition.Id}/versions/1/publish", ctx.Token);
        return definition.Id;
    }

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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? bearer, object? body = null, (string, string)[]? headers = null)
    {
        using var client = api.Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.Add(key, value);
            }
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}
