using Raun;
using Raun.Model;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The runner-neutral scenario graph model and stable identity scheme. Validation guards the DAG
/// invariants the scheduler relies on; ids must be deterministic and never derived from line
/// numbers so reports stay stable across edits above the step.
/// </summary>
public class ModelTests
{
    private static ScenarioNode Node(int index, params int[] dependsOn) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Nodes = nodes,
    };

    [Fact]
    public void Validate_accepts_a_linear_graph()
    {
        var def = Def(Node(0), Node(1, 0), Node(2, 1));

        def.Validate(); // does not throw
    }

    [Fact]
    public void Validate_rejects_a_dependency_cycle()
    {
        var def = Def(Node(0, 1), Node(1, 0));

        var ex = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_out_of_range_dependency()
    {
        var def = Def(Node(0), Node(1, 5));

        Assert.Throws<InvalidOperationException>(def.Validate);
    }

    [Fact]
    public void Scenario_id_is_deterministic_for_the_same_method()
    {
        Assert.Equal(StableId.ForScenario("Ns.Booking"), StableId.ForScenario("Ns.Booking"));
    }

    [Fact]
    public void Scenario_id_differs_by_method()
    {
        Assert.NotEqual(StableId.ForScenario("Ns.Booking"), StableId.ForScenario("Ns.Import"));
    }

    [Fact]
    public void Step_id_is_deterministic_and_keyed()
    {
        var a = StableId.ForStep("scn", "PatientExists:0");
        var b = StableId.ForStep("scn", "PatientExists:0");
        var c = StableId.ForStep("scn", "AvailableSlot:1");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEmpty(a);
    }

    private static ScenarioNode Cond(int index, params int[] dependsOn) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Cond{index}",
        DisplayNameTemplate = $"cond {index}",
        DependsOn = dependsOn,
        Invoke = (_, _) => Task.FromResult<object?>(true),
        EvaluateCondition = static o => (bool)o!,
    };

    private static ScenarioNode Guarded(int index, Guard[] guards, params int[] dependsOn) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "When",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn,
        Guards = guards,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioNode Merge(int index, params int[] sources) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "When",
        OperationName = "Merge",
        DisplayNameTemplate = "«merge»",
        DependsOn = [],
        MergeSources = sources,
        IsSynthetic = true,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    [Fact]
    public void Validate_accepts_a_guarded_graph_with_a_merge()
    {
        var def = Def(
            Cond(0),
            Guarded(1, [new Guard(0, true)], 0),
            Guarded(2, [new Guard(0, false)], 0),
            Merge(3, 1, 2));

        def.Validate(); // does not throw
    }

    [Fact]
    public void Validate_rejects_an_out_of_range_guard_condition()
    {
        var def = Def(Cond(0), Guarded(1, [new Guard(9, true)], 0));

        var ex = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("guard", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_a_guard_on_a_node_without_a_condition_evaluator()
    {
        // Node 0 is a plain step: it has no EvaluateCondition, so it cannot gate a branch.
        var def = Def(Node(0), Guarded(1, [new Guard(0, true)], 0));

        var ex = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("EvaluateCondition", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_merge_sources_that_are_not_mutually_exclusive()
    {
        // Both sources are guarded on the SAME value, so both could pass — a double-write.
        var def = Def(
            Cond(0),
            Guarded(1, [new Guard(0, true)], 0),
            Guarded(2, [new Guard(0, true)], 0),
            Merge(3, 1, 2));

        var ex = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_an_out_of_range_merge_source()
    {
        var def = Def(Cond(0), Merge(1, 7));

        Assert.Throws<InvalidOperationException>(def.Validate);
    }

    [Fact]
    public void Validate_detects_a_cycle_through_merge_sources()
    {
        // Merge sources are real edges: a cycle through them must be caught like a DependsOn cycle.
        var a = Merge(0, 1);
        var b = Merge(1, 0);
        var def = Def(a, b);

        var ex = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_accepts_a_single_source_pass_through()
    {
        // A one-source merge is an alias (the no-`else` pass-through) — exclusivity is vacuous.
        var def = Def(Cond(0), Merge(1, 0));

        def.Validate(); // does not throw
    }

    [Fact]
    public void Step_uid_is_scenario_id_colon_step_id()
    {
        // The one spelling every adapter and sink must agree on, so a filter built from a discovered
        // node resolves to the node the run reports.
        Assert.Equal("scn:b", StepUid.Of("scn", "b"));
    }

    [Fact]
    public void Namespace_and_type_name_default_to_empty_so_an_older_generator_still_binds()
    {
        // Optional, not required: generated code from before these members existed must still
        // compile against this runtime, and readers fall back to splitting MethodName.
        var def = Def(Node(0));

        Assert.Equal("", def.Namespace);
        Assert.Equal("", def.TypeName);
    }
}
