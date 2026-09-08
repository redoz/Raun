using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A DSL step called with an explicit type argument keeps it. The generator used to re-spell the
/// call from the member's identifier alone (<c>member.Name.Identifier.Text</c>), so
/// <c>Given.DefaultOf&lt;int&gt;()</c> lowered to <c>Given.DefaultOf()</c>; building the call from
/// the original invocation node carries the type argument list along with it.
/// </summary>
public class GenericStepLoweringTests
{
    private static GeneratorResult Generate() =>
        GeneratorHarness.Run(SampleSources.Dsl + SampleSources.GenericStepScenario);

    [Fact]
    public void Keeps_an_explicit_type_argument()
    {
        var result = Generate();

        // Without the type argument the call does not compile at all (nothing infers T), so
        // AssertCompiles is half the assertion; the other half is that it is spelled, not inferred.
        result.AssertCompiles();
        Assert.Contains("DefaultOf<int>()", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generated_graph_executes_successfully()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.Equal(3, results.Count);   // DefaultOf, ValueShouldBe, teardown
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal("the value should be 0", results[1].DisplayName);
    }
}
