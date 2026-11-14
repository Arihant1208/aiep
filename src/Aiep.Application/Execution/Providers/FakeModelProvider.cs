using System.Security.Cryptography;
using System.Text;

namespace Aiep.Application.Execution.Providers;

/// <summary>
/// The deterministic fake provider (ADR-0009): free and reproducible, so execution works in CI and load tests without
/// a real model or budget. Output and token counts derive only from the request, so the same request always yields the
/// same response. It is pure .NET with no external dependency, so it lives in Application; the real Anthropic adapter
/// (HTTP, secrets) will be an Infrastructure adapter behind the same <see cref="IModelProvider"/>.
/// </summary>
public sealed class FakeModelProvider : IModelProvider
{
    public const string ProviderName = "fake";

    public string Name => ProviderName;

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = $"echo:{request.Model}:{Fingerprint(request.Prompt)}";
        var response = new ModelResponse(
            text,
            InputTokens: EstimateTokens(request.Prompt),
            OutputTokens: EstimateTokens(text),
            Latency: TimeSpan.FromMilliseconds(5));
        return Task.FromResult(response);
    }

    // ~4 characters per token, at least one.
    private static int EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
