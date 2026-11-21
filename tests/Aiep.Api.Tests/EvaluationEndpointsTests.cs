using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Evaluation endpoints (ADR-0014, ADR-0015): dataset authoring, creating runs, the two-run comparison guard, and
/// workspace-scoped authorization. Execution is proven in the database tests; here the API host has no worker.</summary>
[Collection(ApiCollection.Name)]
public sealed class EvaluationEndpointsTests(ApiFixture api)
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

    private static object OneExample => new { examples = new[] { new { input = new { q = "a" }, expected = new { a = 1 } } } };

    [Fact]
    public async Task Author_a_dataset_create_two_runs_and_compare_them()
    {
        var ctx = await NewWorkspaceAsync("Eval Co");
        var definition = await PublishedDefinitionAsync(ctx, "cfg");
        var suiteVersion = await PublishedSuiteAsync(ctx, "set");

        var runA = await CreateRunAsync(ctx, suiteVersion.Suite, suiteVersion.Version, definition);
        var runB = await CreateRunAsync(ctx, suiteVersion.Suite, suiteVersion.Version, definition);
        Assert.StartsWith("evr_", runA.Id);
        Assert.Equal("Running", runA.Status);
        Assert.Equal(1, runA.ExampleCount);

        var compare = await SendAsync(HttpMethod.Get, $"{ctx.Runs}/compare?a={runA.Id}&b={runB.Id}", ctx.Token);
        Assert.Equal(HttpStatusCode.OK, compare.StatusCode);
        var comparison = (await compare.Content.ReadFromJsonAsync<EvaluationComparisonResponse>())!;
        Assert.Equal(runA.Id, comparison.A.Id);
        Assert.Equal(runB.Id, comparison.B.Id);

        var results = await SendAsync(HttpMethod.Get, $"{ctx.Runs}/{runA.Id}/results", ctx.Token);
        Assert.Equal(HttpStatusCode.OK, results.StatusCode);
        var rows = (await results.Content.ReadFromJsonAsync<List<EvaluationResultRowResponse>>())!;
        Assert.Single(rows);
    }

    [Fact]
    public async Task Comparing_runs_on_different_datasets_is_400()
    {
        var ctx = await NewWorkspaceAsync("Mismatch Co");
        var definition = await PublishedDefinitionAsync(ctx, "cfg");
        var first = await PublishedSuiteAsync(ctx, "one");
        var second = await PublishedSuiteAsync(ctx, "two");

        var runA = await CreateRunAsync(ctx, first.Suite, first.Version, definition);
        var runB = await CreateRunAsync(ctx, second.Suite, second.Version, definition);

        var compare = await SendAsync(HttpMethod.Get, $"{ctx.Runs}/compare?a={runA.Id}&b={runB.Id}", ctx.Token);

        Assert.Equal(HttpStatusCode.BadRequest, compare.StatusCode);
        Assert.Equal("evaluation.different_datasets", await CodeAsync(compare));
    }

    [Fact]
    public async Task A_viewer_cannot_author_evaluations_but_can_read()
    {
        var ctx = await NewWorkspaceAsync("Viewer Co");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        var bobSubject = await SubjectOfAsync(bob);
        await AddMemberAsync(ctx.Org, bobSubject, "Member");
        await SendAsync(HttpMethod.Put,
            $"/v1/organizations/{ctx.Org}/workspaces/{ctx.Ws}/grants/{ResourceId.Format(ResourceId.Subject, bobSubject)}",
            ctx.Token, new { role = "Viewer" });

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, ctx.Suites, bob)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Post, ctx.Suites, bob, new { slug = "x", name = "X" })).StatusCode);
    }

    private sealed record WorkspaceContext(string Token, string Org, string Ws)
    {
        public string Definitions => $"/v1/organizations/{Org}/workspaces/{Ws}/task-definitions";

        public string Suites => $"/v1/organizations/{Org}/workspaces/{Ws}/evaluation-suites";

        public string Runs => $"/v1/organizations/{Org}/workspaces/{Ws}/evaluation-runs";
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

    private async Task<(string Suite, int Version)> PublishedSuiteAsync(WorkspaceContext ctx, string slug)
    {
        var suite = (await (await SendAsync(HttpMethod.Post, ctx.Suites, ctx.Token, new { slug, name = slug }))
            .Content.ReadFromJsonAsync<EvaluationSuiteResponse>())!;
        var version = (await (await SendAsync(HttpMethod.Post, $"{ctx.Suites}/{suite.Id}/versions", ctx.Token, OneExample))
            .Content.ReadFromJsonAsync<EvaluationSuiteVersionResponse>())!;
        await SendAsync(HttpMethod.Post, $"{ctx.Suites}/{suite.Id}/versions/{version.Version}/publish", ctx.Token);
        return (suite.Id, version.Version);
    }

    private async Task<EvaluationRunResponse> CreateRunAsync(WorkspaceContext ctx, string suite, int suiteVersion, string definition)
    {
        var response = await SendAsync(HttpMethod.Post, ctx.Runs, ctx.Token, new
        {
            evaluationSuiteId = suite,
            suiteVersion,
            taskDefinitionId = definition,
            taskDefinitionVersion = 1,
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EvaluationRunResponse>())!;
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
