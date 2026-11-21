using System.Text.Json;

namespace Aiep.Application.Evaluation;

/// <summary>One scorer's verdict on one example: a numeric score and whether it passed.</summary>
public sealed record ScoreOutcome(decimal Score, bool Passed);

/// <summary>
/// A versioned scorer (ADR-0014). Every result records the scorer version. Scorers are pure functions over the stored
/// output and expected value, run by the worker/host in .NET.
/// </summary>
public interface IScorer
{
    string Name { get; }

    string Version { get; }

    ScoreOutcome Score(string outputJson, string expectedJson);
}

/// <summary>Exact match: the output JSON is deeply equal to the expected JSON (object key order ignored).</summary>
public sealed class ExactMatchScorer : IScorer
{
    public string Name => "exact_match";

    public string Version => "1";

    public ScoreOutcome Score(string outputJson, string expectedJson)
    {
        var match = TryEqual(outputJson, expectedJson);
        return new ScoreOutcome(match ? 1m : 0m, match);
    }

    private static bool TryEqual(string a, string b)
    {
        try
        {
            using var left = JsonDocument.Parse(a);
            using var right = JsonDocument.Parse(b);
            return DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    private static bool DeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var bProps = b.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                if (aProps.Count != bProps.Count)
                {
                    return false;
                }

                for (var i = 0; i < aProps.Count; i++)
                {
                    if (!string.Equals(aProps[i].Name, bProps[i].Name, StringComparison.Ordinal)
                        || !DeepEquals(aProps[i].Value, bProps[i].Value))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                if (aItems.Count != bItems.Count)
                {
                    return false;
                }

                for (var i = 0; i < aItems.Count; i++)
                {
                    if (!DeepEquals(aItems[i], bItems[i]))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return a.GetRawText() == b.GetRawText();

            default:
                return a.ValueKind == b.ValueKind; // true/false/null
        }
    }
}
