using System.Text.Json;
using System.Text.Json.Serialization;
using Aiep.Domain.Runs;

namespace Aiep.Application.Execution.Handlers;

/// <summary>
/// The MVP handler: render the prompt from the template and the run input, call the configured model provider, and
/// wrap the (untrusted) model text as <c>{"text": ...}</c>. Richer templating and output-schema validation are later
/// additions; for now the output is well-formed by construction.
/// </summary>
public sealed class CompletionHandler(IEnumerable<IModelProvider> providers) : ITaskHandler
{
    public const string TypeName = "completion";

    private readonly IReadOnlyDictionary<string, IModelProvider> _providers =
        providers.ToDictionary(p => p.Name);

    public string HandlerType => TypeName;

    public string Version => "1";

    public async Task<HandlerResult> ExecuteAsync(HandlerInput input, CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(input.ModelProvider, out var provider))
        {
            throw new HandlerException(
                ErrorCategory.InvalidRequest, $"Unknown model provider '{input.ModelProvider}'.");
        }

        var prompt = RenderPrompt(input.PromptTemplate, input.InputJson);
        var response = await provider.CompleteAsync(new ModelRequest(input.ModelName, prompt), cancellationToken);

        var output = JsonSerializer.Serialize(new OutputEnvelope(response.Text));
        var usage = new ModelUsage(response.InputTokens, response.OutputTokens, response.Latency);
        return new HandlerResult(output, usage);
    }

    private static string RenderPrompt(string? template, string inputJson)
    {
        // No template: the input itself is the prompt. Otherwise a minimal {{input}} substitution.
        return string.IsNullOrEmpty(template)
            ? inputJson
            : template.Replace("{{input}}", inputJson, StringComparison.Ordinal);
    }

    private sealed record OutputEnvelope([property: JsonPropertyName("text")] string Text);
}
