namespace Aiep.Application.Execution;

/// <summary>
/// Payload size limits that bound a run's footprint (threat T12). Input is checked at submission; output is checked after
/// the handler returns, before it is persisted. Both are measured as UTF-8 byte length of the JSON.
/// </summary>
public static class ExecutionLimits
{
    public const int MaxInputBytes = 256 * 1024;

    public const int MaxOutputBytes = 1024 * 1024;
}
