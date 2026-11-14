using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiep.Api.Endpoints;
using Aiep.Application.Ids;
using Aiep.Testing;

namespace Aiep.Api.Tests;

/// <summary>Member endpoints (ADR-0010, ADR-0015): list, add, change role, remove, with Owner-protection invariants.</summary>
[Collection(ApiCollection.Name)]
public sealed class MemberEndpointsTests(ApiFixture api)
{
    private readonly TenantDb _tdb = new(api.Database);

    [Fact]
    public async Task An_owner_sees_themselves_and_can_add_a_member()
    {
        var (alice, org) = await NewOrgAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(await BobAsync()));

        var added = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var members = await (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/members", alice))
            .Content.ReadFromJsonAsync<List<MemberResponse>>();
        Assert.Equal(2, members!.Count);
        Assert.Contains(members, m => m.OrgRole == "Owner");
        Assert.Contains(members, m => m.SubjectId == bobId && m.OrgRole == "Member");
    }

    [Fact]
    public async Task A_member_without_members_read_cannot_list()
    {
        var (alice, org) = await NewOrgAsync();
        var bob = await BobAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/members", bob)).StatusCode);
    }

    [Fact]
    public async Task A_non_member_cannot_list()
    {
        var (_, org) = await NewOrgAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/members", await BobAsync())).StatusCode);
    }

    [Fact]
    public async Task Adding_a_nonexistent_subject_is_404()
    {
        var (alice, org) = await NewOrgAsync();
        var ghost = ResourceId.Format(ResourceId.Subject, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = ghost, role = "Member" })).StatusCode);
    }

    [Fact]
    public async Task Adding_an_existing_member_is_409()
    {
        var (alice, org) = await NewOrgAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(await BobAsync()));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        var again = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("member.already_exists", await CodeAsync(again));
    }

    [Fact]
    public async Task An_admin_cannot_add_an_owner()
    {
        var (alice, org) = await NewOrgAsync();
        var bob = await BobAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Admin" });
        var carol = ResourceId.Format(ResourceId.Subject, await Seed.SubjectAsync(_tdb, "carol"));

        var response = await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", bob, new { subjectId = carol, role = "Owner" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_can_change_a_role()
    {
        var (alice, org) = await NewOrgAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(await BobAsync()));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        var changed = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/members/{bobId}", alice, new { role = "Admin" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("Admin", (await changed.Content.ReadFromJsonAsync<MemberResponse>())!.OrgRole);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted()
    {
        var (alice, org) = await NewOrgAsync();
        var aliceId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(alice));

        var response = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/members/{aliceId}", alice, new { role = "Admin" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("organization.last_owner", await CodeAsync(response));
    }

    [Fact]
    public async Task An_admin_cannot_change_an_owner()
    {
        var (alice, org) = await NewOrgAsync();
        var aliceId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(alice));
        var bob = await BobAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Admin" });

        var response = await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/members/{aliceId}", bob, new { role = "Admin" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_owner_can_remove_a_member()
    {
        var (alice, org) = await NewOrgAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(await BobAsync()));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Delete, $"/v1/organizations/{org}/members/{bobId}", alice)).StatusCode);

        var members = await (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/members", alice))
            .Content.ReadFromJsonAsync<List<MemberResponse>>();
        Assert.Single(members!);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_removed()
    {
        var (alice, org) = await NewOrgAsync();
        var aliceId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(alice));

        var response = await SendAsync(HttpMethod.Delete, $"/v1/organizations/{org}/members/{aliceId}", alice);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("organization.last_owner", await CodeAsync(response));
    }

    [Fact]
    public async Task Removing_a_member_cascades_their_workspace_grants()
    {
        var (alice, org) = await NewOrgAsync();
        var ws = (await (await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/workspaces", alice, new { slug = "w", name = "W" }))
            .Content.ReadFromJsonAsync<WorkspaceResponse>())!;
        var bob = await BobAsync();
        var bobId = ResourceId.Format(ResourceId.Subject, await SubjectOfAsync(bob));
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });
        await SendAsync(HttpMethod.Put, $"/v1/organizations/{org}/workspaces/{ws.Id}/grants/{bobId}", alice, new { role = "Developer" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces/{ws.Id}", bob)).StatusCode);

        // Remove and re-add bob as a plain Member: the old grant must be gone, so the workspace is invisible again.
        await SendAsync(HttpMethod.Delete, $"/v1/organizations/{org}/members/{bobId}", alice);
        await SendAsync(HttpMethod.Post, $"/v1/organizations/{org}/members", alice, new { subjectId = bobId, role = "Member" });

        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(HttpMethod.Get, $"/v1/organizations/{org}/workspaces/{ws.Id}", bob)).StatusCode);
    }

    private Task<string> BobAsync() => api.GetTokenAsync("bob", "bob-local-dev");

    private async Task<(string Token, string Org)> NewOrgAsync()
    {
        var alice = await api.GetTokenAsync("alice", "alice-local-dev");
        var org = (await (await SendAsync(HttpMethod.Post, "/v1/organizations", alice, new { name = "Members Co" }))
            .Content.ReadFromJsonAsync<OrganizationResponse>())!;
        return (alice, org.Id);
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
