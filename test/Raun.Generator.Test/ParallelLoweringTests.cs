using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>Tuple, explicit-array, and LINQ <c>.ToArray()</c> forms lower into parallel sibling groups.</summary>
public class ParallelLoweringTests
{
    [Fact]
    public void Tuple_elements_become_parallel_siblings()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.TupleScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(6, def.Nodes.Count);
        Assert.Empty(def.Nodes[0].DependsOn);                 // DatabaseIsClean
        Assert.Equal([0], def.Nodes[1].DependsOn);             // PatientExists
        Assert.Equal([0], def.Nodes[2].DependsOn);             // AvailableSlot
        Assert.Equal([1, 2], def.Nodes[3].DependsOn);          // CreateAppointment joins both

        Assert.NotNull(def.Nodes[1].GroupId);
        Assert.Equal(def.Nodes[1].GroupId, def.Nodes[2].GroupId);
        Assert.NotEqual(def.Nodes[1].GroupId, def.Nodes[3].GroupId);
    }

    [Fact]
    public async Task Tuple_scenario_runs()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.TupleScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public void Array_elements_become_siblings_and_consumer_rebuilds_array()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.ArrayScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(5, def.Nodes.Count);
        Assert.Empty(def.Nodes[0].DependsOn);                 // UserExists alice
        Assert.Empty(def.Nodes[1].DependsOn);                 // UserExists bob
        Assert.Equal(def.Nodes[0].GroupId, def.Nodes[1].GroupId);
        Assert.Equal([0, 1], def.Nodes[2].DependsOn);          // ImportUsers consumes the array
        Assert.Equal([0, 1, 2], def.Nodes[3].DependsOn);       // ImportShouldContainUsers (import + array)
    }

    [Fact]
    public async Task Array_scenario_runs_and_rebuilds_the_array()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.ArrayScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        // ImportUsers asserts users.Length == 2 implicitly by returning Import(Count); all pass means
        // the array was reconstructed from both sibling outputs.
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public void Linq_range_select_toarray_unrolls_to_one_node_per_element()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinqScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(6, def.Nodes.Count);                      // 3 UserExists + ImportUsers + assertion
        Assert.Empty(def.Nodes[0].DependsOn);
        Assert.Empty(def.Nodes[1].DependsOn);
        Assert.Empty(def.Nodes[2].DependsOn);
        Assert.Equal([0, 1, 2], def.Nodes[3].DependsOn);
        Assert.Equal(def.Nodes[0].GroupId, def.Nodes[2].GroupId);
    }

    [Fact]
    public async Task Linq_scenario_runs()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinqScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public void Void_tuple_elements_become_parallel_siblings_without_a_binding()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.VoidTupleScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(6, def.Nodes.Count);                      // 2 PatientExists + 2 Greet + DatabaseIsClean + teardown
        Assert.Equal([0, 1], def.Nodes[2].DependsOn);          // Greet jane: data dep on jane, order dep on the step before the group
        Assert.Equal([1], def.Nodes[3].DependsOn);             // Greet bob
        Assert.Equal(def.Nodes[2].GroupId, def.Nodes[3].GroupId);
        Assert.Equal([2, 3], def.Nodes[4].DependsOn);          // DatabaseIsClean joins both
    }

    [Fact]
    public async Task Void_tuple_scenario_runs()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.VoidTupleScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public void Void_array_elements_become_parallel_siblings_without_a_binding()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.VoidArrayScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(6, def.Nodes.Count);
        Assert.Equal([0, 1], def.Nodes[2].DependsOn);
        Assert.Equal([1], def.Nodes[3].DependsOn);
        Assert.Equal(def.Nodes[2].GroupId, def.Nodes[3].GroupId);
        Assert.Equal([2, 3], def.Nodes[4].DependsOn);
    }

    [Fact]
    public async Task Void_array_scenario_runs()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.VoidArrayScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public void Void_linq_array_elements_become_parallel_siblings_without_a_binding()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.VoidLinqScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(6, def.Nodes.Count);                      // PatientExists + 3 Greet + DatabaseIsClean + teardown
        Assert.Equal([0], def.Nodes[1].DependsOn);
        Assert.Equal([0], def.Nodes[2].DependsOn);
        Assert.Equal([0], def.Nodes[3].DependsOn);
        Assert.Equal(def.Nodes[1].GroupId, def.Nodes[3].GroupId);
        Assert.Equal([1, 2, 3], def.Nodes[4].DependsOn);
    }

    [Fact]
    public async Task Void_linq_scenario_runs()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.VoidLinqScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }
}
