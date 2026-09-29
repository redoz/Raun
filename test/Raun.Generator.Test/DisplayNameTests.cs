using Xunit;

namespace Raun.Generator.Test;

/// <summary>Display-name placeholders: constants substituted at compile time, runtime values formatted at run.</summary>
public class DisplayNameTests
{
    [Fact]
    public void Runtime_placeholder_emits_a_format_delegate()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.RuntimeNameScenario);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        var greet = def.Nodes[2];
        Assert.NotNull(greet.FormatDisplayName);
        Assert.Contains("{patient}", greet.DisplayNameTemplate); // unresolved fallback template
    }

    [Fact]
    public async Task Runtime_placeholder_resolves_against_the_produced_value()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.RuntimeNameScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.Contains("Jane", results[2].DisplayName); // formatted from the Patient output
    }
}
