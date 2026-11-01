namespace Aiep.Domain.Runs;

/// <summary>
/// Lifecycle states of a logical task run. See docs/architecture/task-lifecycle.md.
/// </summary>
public enum TaskRunStatus
{
    Accepted,
    Queued,
    Running,
    RetryWait,
    Succeeded,
    Failed,
    Cancelled,
    DeadLettered,
}
