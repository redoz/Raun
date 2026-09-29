using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>A linear scenario lowers into a source-order + dataflow dependency chain that runs.</summary>
public class LinearLoweringTests
{
    private static GeneratorResult Generate() =>
        GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);

    [Fact]
    public void Lowers_into_dependency_chain()
    {
        var result = Generate();
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());

        Assert.Equal("booking", def.DisplayName);
        Assert.Equal("Demo.BookingScenarios.Booking", def.MethodName);
        Assert.Equal(6, def.Nodes.Count);

        Assert.Equal([0], def.Nodes[1].DependsOn);              // PatientExists
        Assert.Equal([0, 1], def.Nodes[2].DependsOn);          // AvailableSlot (source order)
        Assert.Equal([0, 1, 2], def.Nodes[3].DependsOn);       // CreateAppointment (dataflow patient+slot)
        Assert.Equal([0, 3], def.Nodes[4].DependsOn);          // AppointmentExists (dataflow appointment)

        Assert.Equal("Given", def.Nodes[1].Phase);
        Assert.Equal("When", def.Nodes[3].Phase);
        Assert.Equal("Then", def.Nodes[4].Phase);

        Assert.Equal("CreateAppointment", def.Nodes[3].OperationName);
    }

    [Fact]
    public void Substitutes_constant_display_name_placeholders()
    {
        var def = Generate().Definitions().Single();

        Assert.Equal("patient Jane exists", def.Nodes[1].DisplayNameTemplate);
        Assert.Null(def.Nodes[1].FormatDisplayName);
    }

    [Fact]
    public async Task Generated_graph_executes_successfully()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal("patient Jane exists", results[1].DisplayName);
    }
}
