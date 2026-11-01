using System.Net;
using Aiep.Api.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aiep.Api.Tests;

public sealed class HostSmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Liveness_is_healthy_without_external_dependencies()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_a_generated_correlation_id()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.True(response.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values));
        Assert.False(string.IsNullOrWhiteSpace(values.Single()));
    }

    [Fact]
    public async Task A_safe_client_correlation_id_is_echoed()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "client-abc_123");

        var response = await client.SendAsync(request);

        Assert.Equal("client-abc_123", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    public static TheoryData<string> UnsafeCorrelationIds => new()
    {
        "contains spaces",
        "<script>",
        new string('a', 65),
    };

    [Theory]
    [MemberData(nameof(UnsafeCorrelationIds))]
    public async Task An_unsafe_client_correlation_id_is_replaced(string supplied)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, supplied);

        var response = await client.SendAsync(request);

        Assert.NotEqual(supplied, response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
