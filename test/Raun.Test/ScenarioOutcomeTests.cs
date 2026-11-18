using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// How a scenario ended, decided from its step results in one place: the answer every reporter of a
/// whole scenario reads.
/// </summary>
public class ScenarioOutcomeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly ScenarioNode[] Nodes =
    [
        Node(0, setup: true),
        Node(1),
        Node(2),
        Node(3, synthetic: true),
        Node(4, teardown: true),
    ];

    private static ScenarioNode Node(int index, bool setup = false, bool teardown = false, bool synthetic = false) => new()
    {
        Index = index,
        StepId = "step-" + index,
        Phase = "Given",
        OperationName = "Op" + index,
        DisplayNameTemplate = "op " + index,
        DependsOn = [],
        Kind = setup ? NodeKind.Setup : teardown ? NodeKind.Teardown : synthetic ? NodeKind.Case : NodeKind.Step,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static StepResult Step(int index, StepStatus status, string? skipReason = null, Exception? exception = null) => new()
    {
        Node = Nodes[index],
        TitleMessage = StepMessage.FromText(Nodes[index].DisplayNameTemplate),
        Status = status,
        StartedAt = Start + TimeSpan.FromSeconds(index),
        SkipReason = skipReason,
        Exception = exception,
    };

    private static StepResult Passed(int index) => Step(index, StepStatus.Passed);

    private static StepResult Skipped(int index, string reason) => Step(index, StepStatus.Skipped, reason);

    [Fact]
    public void Every_step_passing_is_passed()
    {
        var outcome = ScenarioOutcome.Of([Passed(0), Passed(1), Passed(2), Passed(4)]);

        Assert.Equal(ScenarioStatus.Passed, outcome.Status);
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void The_first_failing_step_decides_a_failure()
    {
        var first = Step(1, StepStatus.Failed, exception: new InvalidOperationException("first"));

        var outcome = ScenarioOutcome.Of(
            [Step(2, StepStatus.Failed, exception: new InvalidOperationException("second")), first, Passed(0), Passed(4)]);

        Assert.Equal(ScenarioStatus.Failed, outcome.Status);
        Assert.Same(first, outcome.Decisive);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void A_step_that_timed_out_decides_even_after_an_earlier_failure()
    {
        var late = Step(2, StepStatus.Failed, exception: new TimeoutException("too slow"));

        var outcome = ScenarioOutcome.Of(
            [Passed(0), Step(1, StepStatus.Failed, exception: new InvalidOperationException("x")), late, Passed(4)]);

        Assert.Equal(ScenarioStatus.TimedOut, outcome.Status);
        Assert.Same(late, outcome.Decisive);
    }

    [Fact]
    public void A_scenario_deadline_that_left_steps_unrun_is_a_timeout_whatever_passed_before()
    {
        var outcome = ScenarioOutcome.Of(
            [Passed(0), Passed(1), Skipped(2, ScenarioScheduler.TimedOutSkipReason), Passed(4)]);

        Assert.Equal(ScenarioStatus.TimedOut, outcome.Status);
        Assert.Null(outcome.Decisive);
        Assert.Equal(ScenarioScheduler.TimedOutSkipReason, outcome.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cancelled_scenario_is_cancelled_whether_or_not_a_step_had_passed(bool oneStepRan)
    {
        var outcome = ScenarioOutcome.Of(
        [
            Passed(0),
            oneStepRan ? Passed(1) : Skipped(1, ScenarioScheduler.CanceledSkipReason),
            Skipped(2, ScenarioScheduler.CanceledSkipReason),
            Passed(4), // teardown passes even after a cancellation
        ]);

        Assert.Equal(ScenarioStatus.Canceled, outcome.Status);
        Assert.Equal(ScenarioScheduler.CanceledSkipReason, outcome.Reason);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void A_failure_before_the_cancellation_is_still_a_failure()
    {
        var failed = Step(1, StepStatus.Failed, exception: new InvalidOperationException("x"));

        var outcome = ScenarioOutcome.Of([Passed(0), failed, Skipped(2, ScenarioScheduler.CanceledSkipReason), Passed(4)]);

        Assert.Equal(ScenarioStatus.Failed, outcome.Status);
    }

    [Fact]
    public void Setup_and_teardown_passing_alone_is_not_a_scenario_that_ran()
    {
        var outcome = ScenarioOutcome.Of(
        [
            Passed(0),
            Skipped(1, ScenarioScheduler.NotSelectedSkipReason),
            Skipped(2, ScenarioScheduler.NotSelectedSkipReason),
            Passed(4),
        ]);

        Assert.Equal(ScenarioStatus.NotRun, outcome.Status);
        Assert.Equal(ScenarioScheduler.NotSelectedSkipReason, outcome.Reason);
        Assert.True(outcome.Succeeded); // nothing failed: an "on success" teardown still cleans up
    }

    [Fact]
    public void A_failing_teardown_fails_the_scenario()
    {
        var teardown = Step(4, StepStatus.Failed, exception: new InvalidOperationException("cleanup"));

        var outcome = ScenarioOutcome.Of([Passed(0), Passed(1), Passed(2), teardown]);

        Assert.Equal(ScenarioStatus.Failed, outcome.Status);
        Assert.Same(teardown, outcome.Decisive);
    }

    [Fact]
    public void A_step_the_run_did_not_select_and_a_branch_not_taken_count_neither_way()
    {
        var outcome = ScenarioOutcome.Of(
        [
            Passed(0),
            Passed(1),
            Step(2, StepStatus.NotTaken, "not taken: else"),
            Passed(4),
        ]);

        Assert.Equal(ScenarioStatus.Passed, outcome.Status);
        Assert.Equal(
            ScenarioStatus.Passed,
            ScenarioOutcome.Of([Passed(0), Passed(1), Skipped(2, ScenarioScheduler.NotSelectedSkipReason), Passed(4)]).Status);
    }

    [Fact]
    public void Plumbing_results_take_no_part()
    {
        Assert.Equal(ScenarioStatus.NotRun, ScenarioOutcome.Of([Step(3, StepStatus.Passed)]).Status);
        Assert.Equal(
            ScenarioStatus.Passed,
            ScenarioOutcome.Of([Passed(1), Step(3, StepStatus.Skipped, ScenarioScheduler.CanceledSkipReason)]).Status);
    }

    [Fact]
    public void No_results_at_all_is_not_run()
    {
        var outcome = ScenarioOutcome.Of([]);

        Assert.Equal(ScenarioStatus.NotRun, outcome.Status);
        Assert.Equal("skipped", outcome.Reason);
    }
}
