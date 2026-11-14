using System.Text.Json;
using Aiep.Application.Execution;
using Aiep.Application.Execution.Handlers;
using Aiep.Application.Execution.Providers;
using Aiep.Domain.Runs;

namespace Aiep.Application.Tests.Execution;

public sealed class FakeModelProviderTests
{
    private static ModelRequest Request(string model = "fake-1", string prompt = "hello") => new(model, prompt);

    [Fact]
    public async Task The_same_request_always_produces_the_same_response()
    {
        var provider = new FakeModelProvider();

        var first = await provider.CompleteAsync(Request(), CancellationToken.None);
        var second = await provider.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.True(first.InputTokens > 0);
        Assert.True(first.OutputTokens > 0);
    }

    [Fact]
    public async Task Different_prompts_produce_different_output()
    {
        var provider = new FakeModelProvider();

        var a = await provider.CompleteAsync(Request(prompt: "one"), CancellationToken.None);
        var b = await provider.CompleteAsync(Request(prompt: "two"), CancellationToken.None);

        Assert.NotEqual(a.Text, b.Text);
    }

    [Fact]
    public async Task A_cancelled_token_is_observed()
    {
        var provider = new FakeModelProvider();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(), cts.Token));
    }
}

public sealed class CompletionHandlerTests
{
    private static HandlerInput Input(
        string provider = FakeModelProvider.ProviderName, string? template = null, string input = "\"hi\"") =>
        new(CompletionHandler.TypeName, "1", provider, "fake-1", "{}", template, input, "{}");

    [Fact]
    public async Task It_calls_the_provider_and_wraps_the_output_as_json_with_usage()
    {
        var handler = new CompletionHandler([new FakeModelProvider()]);

        var result = await handler.ExecuteAsync(Input(), CancellationToken.None);

        using var document = JsonDocument.Parse(result.OutputJson);
        var text = document.RootElement.GetProperty("text").GetString();
        Assert.StartsWith("echo:fake-1:", text);
        Assert.True(result.Usage.InputTokens > 0);
        Assert.True(result.Usage.OutputTokens > 0);
    }

    [Fact]
    public async Task It_substitutes_the_input_into_the_prompt_template()
    {
        var spy = new RecordingProvider();
        var handler = new CompletionHandler([spy]);

        await handler.ExecuteAsync(Input(provider: RecordingProvider.ProviderName, template: "answer: {{input}}"),
            CancellationToken.None);

        Assert.Equal("answer: \"hi\"", spy.LastPrompt);
    }

    [Fact]
    public async Task An_unknown_provider_is_a_permanent_invalid_request()
    {
        var handler = new CompletionHandler([new FakeModelProvider()]);

        var ex = await Assert.ThrowsAsync<HandlerException>(() =>
            handler.ExecuteAsync(Input(provider: "nonexistent"), CancellationToken.None));

        Assert.Equal(ErrorCategory.InvalidRequest, ex.Category);
    }

    [Fact]
    public async Task A_provider_failure_propagates_with_its_category()
    {
        var handler = new CompletionHandler([new FailingProvider()]);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() =>
            handler.ExecuteAsync(Input(provider: FailingProvider.ProviderName), CancellationToken.None));

        Assert.Equal(ErrorCategory.RateLimited, ex.Category);
    }

    private sealed class RecordingProvider : IModelProvider
    {
        public const string ProviderName = "recording";

        public string Name => ProviderName;

        public string? LastPrompt { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            LastPrompt = request.Prompt;
            return Task.FromResult(new ModelResponse("ok", 1, 1, TimeSpan.Zero));
        }
    }

    private sealed class FailingProvider : IModelProvider
    {
        public const string ProviderName = "failing";

        public string Name => ProviderName;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            throw new ModelProviderException(ErrorCategory.RateLimited, "slow down", TimeSpan.FromSeconds(1));
    }
}

public sealed class HandlerRegistryTests
{
    [Fact]
    public void It_resolves_an_allowlisted_handler_and_rejects_anything_else()
    {
        var handler = new CompletionHandler([new FakeModelProvider()]);
        var registry = new HandlerRegistry([handler]);

        Assert.Same(handler, registry.Resolve(CompletionHandler.TypeName, "1"));
        Assert.Null(registry.Resolve(CompletionHandler.TypeName, "2"));
        Assert.Null(registry.Resolve("not-allowlisted", "1"));
    }
}
