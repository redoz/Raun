using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The scheduler hands every step the scenario's <see cref="ResourceLedger"/>, so two steps that
/// nothing orders and that both mutate one identity fail — the later claim throws
/// <see cref="ResourceConflictException"/> — while ordered steps and shared access run as before.
/// </summary>
public class ResourceConflictSchedulingTests
{
    private sealed record Account(string Id) : IResource<Account>
    {
        public static ResourceKey KeyFor(Account instance) => instance.Id;
    }

    private static ScenarioNode Node(
        int index,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        params int[] dependsOn) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "When",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn,
        Invoke = invoke,
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Nodes = nodes,
    };

    /// <summary>A step body that declares <paramref name="verb"/> on <c>Account:{id}</c>, optionally
    /// waiting on <paramref name="gate"/> first so claim order is deterministic.</summary>
    private static Func<IStepInputs, ScenarioContext, Task<object?>> Touch(
        ResourceEffect verb, string id, Task? gate = null, TaskCompletionSource? open = null)
        => async (_, ctx) =>
        {
            if (gate is not null)
            {
                await gate;
            }

            var account = new Account(id);
            switch (verb)
            {
                case ResourceEffect.Create:
                    await ctx.Resources.Create(account);
                    break;
                case ResourceEffect.Update:
                    await ctx.Resources.Update(account);
                    break;
                case ResourceEffect.Delete:
                    await ctx.Resources.Delete(account);
                    break;
                default:
                    await ctx.Resources.Read(account);
                    break;
            }

            open?.SetResult();
            return account;
        };

    private static async Task<IReadOnlyList<StepResult>> Run(ScenarioDefinition def)
    {
        var task = new ScenarioScheduler().RunAsync(def);
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == task, "scheduler did not complete within the test timeout");
        return await task;
    }

    [Fact]
    public async Task Unordered_steps_mutating_one_identity_fail_the_later_claim()
    {
        var firstClaimed = new TaskCompletionSource();
        var def = Def(
            Node(0, Touch(ResourceEffect.Update, "acc-1", open: firstClaimed)),
            Node(1, Touch(ResourceEffect.Delete, "acc-1", gate: firstClaimed.Task)));

        var results = await Run(def);

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Failed, results[1].Status);
        var conflict = Assert.IsType<ResourceConflictException>(results[1].Exception);
        Assert.Equal("op 1", conflict.StepDisplayName);
        Assert.Equal("op 0", conflict.OtherStepDisplayName);
        Assert.Equal("Account:acc-1", conflict.Identity.ToString());
    }

    [Fact]
    public async Task A_refused_claim_records_no_effect_and_its_dependents_skip()
    {
        var firstClaimed = new TaskCompletionSource();
        var def = Def(
            Node(0, Touch(ResourceEffect.Update, "acc-1", open: firstClaimed)),
            Node(1, Touch(ResourceEffect.Update, "acc-1", gate: firstClaimed.Task)),
            Node(2, Touch(ResourceEffect.Read, "acc-1"), 1));

        var results = await Run(def);

        var recorded = Assert.Single(results[0].Effects);
        Assert.Equal(ResourceEffect.Update, recorded.Effect);
        Assert.Empty(results[1].Effects);
        Assert.Equal(StepStatus.Skipped, results[2].Status);
    }

    [Fact]
    public async Task Ordered_steps_mutating_one_identity_both_pass()
    {
        var def = Def(
            Node(0, Touch(ResourceEffect.Create, "acc-1")),
            Node(1, Touch(ResourceEffect.Update, "acc-1"), 0),
            Node(2, Touch(ResourceEffect.Delete, "acc-1"), 1));

        var results = await Run(def);

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task Unordered_shared_access_to_one_identity_both_pass()
    {
        var def = Def(
            Node(0, Touch(ResourceEffect.Read, "acc-1")),
            Node(1, Touch(ResourceEffect.Read, "acc-1")));

        var results = await Run(def);

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task Unordered_mutations_of_different_identities_both_pass()
    {
        var def = Def(
            Node(0, Touch(ResourceEffect.Update, "acc-1")),
            Node(1, Touch(ResourceEffect.Update, "acc-2")));

        var results = await Run(def);

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task Detection_is_structural_not_timing_based()
    {
        // Step 1 only claims after step 0 has fully finished; they never overlap in time. They are
        // still unordered in the graph, so the conflict is reported on every run, not only when the
        // race happens to materialize.
        var firstFinished = new TaskCompletionSource();
        var def = Def(
            Node(0, async (_, ctx) =>
            {
                await ctx.Resources.Update(new Account("acc-1"));
                firstFinished.SetResult();
                return null;
            }),
            Node(1, Touch(ResourceEffect.Update, "acc-1", gate: firstFinished.Task)));

        var results = await Run(def);

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Failed, results[1].Status);
        Assert.IsType<ResourceConflictException>(results[1].Exception);
    }

    [Fact]
    public async Task Effects_and_refused_claims_tell_their_story_in_the_step_log()
    {
        var firstClaimed = new TaskCompletionSource();
        var def = Def(
            Node(0, Touch(ResourceEffect.Update, "acc-1", open: firstClaimed)),
            Node(1, Touch(ResourceEffect.Delete, "acc-1", gate: firstClaimed.Task)));

        var results = await Run(def);

        Assert.Equal(["[resource] Update Account:acc-1"], results[0].Logs);
        var conflictLine = Assert.Single(results[1].Logs);
        Assert.StartsWith("[resource] conflict: ", conflictLine, StringComparison.Ordinal);
        Assert.Contains("Account:acc-1", conflictLine, StringComparison.Ordinal);
    }
}
