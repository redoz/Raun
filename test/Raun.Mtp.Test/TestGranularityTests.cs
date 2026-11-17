using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// How Raun decides whether a scenario is reported as one test per step or as a single test. The
/// per-run override wins, then the suite's own choice in code, then Stryker's presence, then the
/// per-step default.
/// </summary>
public class TestGranularityTests
{
    private static Func<string, string?> Environment(string? overrideValue, string? strykerValue) => name => name switch
    {
        "RAUN_TEST_GRANULARITY" => overrideValue,
        "STRYKER_MUTANT_FILE" => strykerValue,
        _ => null,
    };

    [Theory]
    [InlineData(null, null, null, TestGranularity.Step)]
    [InlineData(null, null, "stryker-mutant-1.txt", TestGranularity.Scenario)]
    [InlineData(null, TestGranularity.Step, "stryker-mutant-1.txt", TestGranularity.Step)]
    [InlineData(null, TestGranularity.Scenario, null, TestGranularity.Scenario)]
    [InlineData("step", TestGranularity.Scenario, "stryker-mutant-1.txt", TestGranularity.Step)]
    [InlineData("SCENARIO", TestGranularity.Step, null, TestGranularity.Scenario)]
    [InlineData(" scenario ", null, null, TestGranularity.Scenario)]
    [InlineData("", null, null, TestGranularity.Step)]
    [InlineData("  ", null, "stryker-mutant-1.txt", TestGranularity.Scenario)]
    [InlineData(null, null, " ", TestGranularity.Step)]
    public void Resolves_in_order_override_then_code_then_stryker_then_step(
        string? overrideValue, TestGranularity? configured, string? strykerValue, TestGranularity expected)
    {
        var resolved = TestGranularityResolver.Resolve(configured, Environment(overrideValue, strykerValue));

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void An_unknown_override_value_fails_naming_the_valid_values()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => TestGranularityResolver.Resolve(null, Environment("scenrio", null)));

        Assert.Contains("scenrio", error.Message, StringComparison.Ordinal);
        Assert.Contains("RAUN_TEST_GRANULARITY", error.Message, StringComparison.Ordinal);
        Assert.Contains("'step'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'scenario'", error.Message, StringComparison.Ordinal);
    }
}
