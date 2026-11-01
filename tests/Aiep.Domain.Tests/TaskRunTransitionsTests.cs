using Aiep.Domain.Runs;

namespace Aiep.Domain.Tests;

public sealed class TaskRunTransitionsTests
{
    [Theory]
    [InlineData(TaskRunStatus.Accepted, TaskRunStatus.Queued)]
    [InlineData(TaskRunStatus.Queued, TaskRunStatus.Running)]
    [InlineData(TaskRunStatus.Running, TaskRunStatus.Succeeded)]
    [InlineData(TaskRunStatus.Running, TaskRunStatus.Failed)]
    [InlineData(TaskRunStatus.Running, TaskRunStatus.RetryWait)]
    [InlineData(TaskRunStatus.Running, TaskRunStatus.DeadLettered)]
    [InlineData(TaskRunStatus.RetryWait, TaskRunStatus.Queued)]
    [InlineData(TaskRunStatus.Queued, TaskRunStatus.Cancelled)]
    [InlineData(TaskRunStatus.Running, TaskRunStatus.Cancelled)]
    public void Allows_documented_transitions(TaskRunStatus from, TaskRunStatus to)
    {
        Assert.True(TaskRunTransitions.CanTransition(from, to));
    }

    [Theory]
    [InlineData(TaskRunStatus.Accepted, TaskRunStatus.Running)]
    [InlineData(TaskRunStatus.Queued, TaskRunStatus.Succeeded)]
    [InlineData(TaskRunStatus.RetryWait, TaskRunStatus.Running)]
    [InlineData(TaskRunStatus.Succeeded, TaskRunStatus.Queued)]
    [InlineData(TaskRunStatus.Failed, TaskRunStatus.RetryWait)]
    [InlineData(TaskRunStatus.Running, TaskRunStatus.Running)]
    public void Rejects_undocumented_transitions(TaskRunStatus from, TaskRunStatus to)
    {
        Assert.False(TaskRunTransitions.CanTransition(from, to));
    }

    [Theory]
    [InlineData(TaskRunStatus.Succeeded)]
    [InlineData(TaskRunStatus.Failed)]
    [InlineData(TaskRunStatus.Cancelled)]
    [InlineData(TaskRunStatus.DeadLettered)]
    public void Terminal_states_have_no_outgoing_transitions(TaskRunStatus status)
    {
        Assert.True(TaskRunTransitions.IsTerminal(status));
        Assert.Empty(TaskRunTransitions.NextStates(status));
    }

    [Fact]
    public void Every_status_is_covered_by_the_transition_table()
    {
        foreach (var status in Enum.GetValues<TaskRunStatus>())
        {
            var isTerminal = TaskRunTransitions.IsTerminal(status);
            var hasNext = TaskRunTransitions.NextStates(status).Count > 0;
            Assert.True(isTerminal ^ hasNext, $"{status} must be either terminal or have next states.");
        }
    }

    [Fact]
    public void Every_non_terminal_state_can_be_cancelled()
    {
        var nonTerminal = Enum.GetValues<TaskRunStatus>().Where(s => !TaskRunTransitions.IsTerminal(s));

        Assert.All(nonTerminal, s => Assert.True(TaskRunTransitions.CanTransition(s, TaskRunStatus.Cancelled)));
    }
}
