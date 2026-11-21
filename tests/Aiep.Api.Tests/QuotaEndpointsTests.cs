using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Budget administration and reserve-then-settle enforcement (threat T12): setting a budget, reading usage, a
/// submission refused with 429 when it would exceed the budget, and the org:manage gate on setting budgets.</summary>
[Collection(ApiCollection.Name)]
public sealed class QuotaEndpointsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    private static object SonnetVersion => new
    {
        inputSchema = new { type = "object" },
        outputSchema = new { type = "object" },
        handlerType = "completion",
        handlerVersion = "1",
        modelProvider = "fake",
        modelName = "fake-sonnet",
    };

    [Fact]
    public async Task Owner_sets_a_budget_and_reads_usage()
    {
        var (alice, org) = await NewOrgAsync("Budget Co");

        var set = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/budget", alice, new { windowSeconds = 86400, limitUsd = 250.0 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var usage = (await set.Content.ReadFromJsonAsync<QuotaUsageResponse>())!;
        Assert.True(usage.HasBudget);
        Assert.Equal(250m, usage.LimitUsd);

        var read = await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/usage", alice);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(250m, (await read.Content.ReadFromJsonAsync<QuotaUsageResponse>())!.LimitUsd);
    }

    [Fact]
    public async Task A_submission_over_budget_is_429()
    {
        var (alice, org) = await NewOrgAsync("Tight Co");
        var ws = (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "main", name = "Main" }))
            .Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
        var runs = $"/v1/organizations/{org}/workspaces/{ws}/runs";
        var definitions = $"/v1/organizations/{org}/workspaces/{ws}/task-definitions";

        // A priced model so the estimate is non-zero, and a tiny budget it will exceed.
        var def = (await (await SendAsync(HttpMethod.Post, definitions, alice, new { slug = "t", name = "T" }))
            .Content.ReadFromJsonAsync<TaskDefinitionResponse>())!;
        await SendAsync(HttpMethod.Post, $"{definitions}/{def.Id}/versions", alice, SonnetVersion);
        await SendAsync(HttpMethod.Post, $"{definitions}/{def.Id}/versions/1/publish", alice);
        await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/budget", alice, new { windowSeconds = 86400, limitUsd = 0.01 });

        var response = await SendAsync(HttpMethod.Post, runs, alice, new { taskDefinitionId = def.Id, input = new { q = "hi" } });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("quota.budget_exceeded", await CodeAsync(response));
    }

    [Fact]
    public async Task A_member_without_org_manage_cannot_set_a_budget()
    {
        var (_, org) = await NewOrgAsync("Closed Co");
        var bob = await api.GetTokenAsync("bob", "bob-local-dev");
        await AddMemberAsync(org, await SubjectOfAsync(bob), "Member");

        var response = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/budget", bob, new { windowSeconds = 86400, limitUsd = 10.0 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(string Token, string Org)> NewOrgAsync(string name)
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        return (alice, org.Id);
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
