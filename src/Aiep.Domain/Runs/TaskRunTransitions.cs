using System.Collections.Frozen;

namespace Aiep.Domain.Runs;

/// <summary>
/// The single source of truth for which run state transitions are legal.
/// All status changes must go through this table; ad hoc status updates are not allowed.
/// </summary>
public static class TaskRunTransitions
{
    private static readonly FrozenDictionary<TaskRunStatus, FrozenSet<TaskRunStatus>> Allowed =
        new Dictionary<TaskRunStatus, FrozenSet<TaskRunStatus>>
        {
            [TaskRunStatus.Accepted] = Set(TaskRunStatus.Queued, TaskRunStatus.Cancelled),
            [TaskRunStatus.Queued] = Set(TaskRunStatus.Running, TaskRunStatus.Cancelled),
            [TaskRunStatus.Running] = Set(
                TaskRunStatus.Succeeded,
                TaskRunStatus.Failed,
                TaskRunStatus.RetryWait,
                TaskRunStatus.DeadLettered,
                TaskRunStatus.Cancelled),
            [TaskRunStatus.RetryWait] = Set(TaskRunStatus.Queued, TaskRunStatus.Cancelled),
            [TaskRunStatus.Succeeded] = Set(),
            [TaskRunStatus.Failed] = Set(),
            [TaskRunStatus.Cancelled] = Set(),
            [TaskRunStatus.DeadLettered] = Set(),
        }.ToFrozenDictionary();

    public static bool CanTransition(TaskRunStatus from, TaskRunStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(TaskRunStatus status) =>
        Allowed.TryGetValue(status, out var targets) && targets.Count == 0;

    public static IReadOnlySet<TaskRunStatus> NextStates(TaskRunStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : FrozenSet<TaskRunStatus>.Empty;

    private static FrozenSet<TaskRunStatus> Set(params TaskRunStatus[] states) => states.ToFrozenSet();
}
