using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Issue #2: a step argument may construct a typed value from constants and earlier results, and may
/// project an earlier result's members. Each read is a dependency on the producing step, the producer
/// runs once, and the argument is evaluated when the consuming step starts. Names the scenario resolved
/// lexically (its class's members, its namespace's types) mean the same thing in the generated code.
/// </summary>
public class StepArgumentLoweringTests
{
    private const int Isolation = 0, Customer = 1, Employee = 2, Clock = 3, Setup = 4, Creation = 5;

    private static GeneratorResult Generate() =>
        GeneratorHarness.Run(SampleSources.CompositionDsl + SampleSources.CompositionScenario);

    private static ScenarioDefinition Scenario(GeneratorResult result, string methodName) =>
        result.Definitions().Single(d => d.MethodName == "Composition.Tests.CompositionScenarios." + methodName);

    [Fact]
    public void An_initializer_depends_on_every_step_output_it_reads()
    {
        var result = Generate();
        result.AssertCompiles();
        var setup = Scenario(result, "RappelFailure").Nodes[Setup];

        Assert.Equal("CreationContext", setup.OperationName);
        Assert.Equal([Isolation, Customer, Employee, Clock], setup.DependsOn);
    }

    [Fact]
    public void A_projection_depends_on_the_step_it_projects()
    {
        var creation = Scenario(Generate(), "RappelFailure").Nodes[Creation];

        Assert.Equal("CreateAppointment", creation.OperationName);
        Assert.Equal([Isolation, Setup], creation.DependsOn);
    }

    [Fact]
    public void A_lambda_parameter_that_shadows_a_step_local_is_not_a_read_of_it()
    {
        var satisfies = Scenario(Generate(), "RappelFailure").Nodes.Single(n => n.OperationName == "Satisfies");

        Assert.DoesNotContain(Employee, satisfies.DependsOn);
        Assert.Contains(Customer, satisfies.DependsOn);
    }

    [Fact]
    public async Task Constructed_and_projected_arguments_carry_the_runtime_values()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "RappelFailure").RunAsync();

        Assert.All(
            results.Where(r => !r.Node.IsTeardown),
            r => Assert.True(r.Status == StepStatus.Passed, r.DisplayName + ": " + r.Exception));
    }

    [Fact]
    public async Task A_throwing_projection_fails_the_consuming_step_under_its_own_name()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "ProjectionThrows").RunAsync();
        var failed = Assert.Single(results, r => r.Status == StepStatus.Failed);

        Assert.Equal("Equal", failed.Node.OperationName);
        Assert.Equal("{actual} equals sixth", failed.DisplayName);
        Assert.IsType<IndexOutOfRangeException>(failed.Exception);
    }

    [Fact]
    public async Task The_sample_is_clean_under_the_analyzer()
    {
        var diagnostics = await GeneratorHarness.AnalyzeAsync(
            SampleSources.CompositionDsl + SampleSources.CompositionScenario, requireCompilable: true);

        Assert.Empty(diagnostics);
    }
}
