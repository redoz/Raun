using Xunit;

namespace Raun.Generator.Test;

public class ScenarioMethodLookupTests
{
    [Fact]
    public void A_helper_sharing_the_scenario_name_does_not_hide_the_scenario_method()
    {
        var source = ScenarioCaseGenerationTests.Dsl + """

            public sealed class Stories : Suite
            {
                [Scenario]
                public async Task Checkout() { await Given.Capture("cart"); }
                private Task Checkout(int retries) => Task.CompletedTask;
                [Scenario]
                public async Task Other() { await Given.Capture("other"); }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();

        var definitions = result.Definitions();

        Assert.Equal(["Checkout", "Other"], definitions.Select(d => d.DisplayName).Order(StringComparer.Ordinal));
        var checkout = definitions.Single(d => d.DisplayName == "Checkout");
        Assert.Empty(checkout.Method!.GetParameters());
    }
}
