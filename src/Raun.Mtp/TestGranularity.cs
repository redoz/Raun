using System.Globalization;

namespace Raun.Mtp;

/// <summary>What Raun reports to the test platform as one test.</summary>
public enum TestGranularity
{
    /// <summary>Every scenario step is its own test. The default.</summary>
    Step,

    /// <summary>
    /// Every scenario is a single test, folded from its steps. For tools that run each test on its
    /// own — a mutation tester such as Stryker.NET — where one test per step means running a
    /// scenario's earlier steps again for every later one.
    /// </summary>
    Scenario,
}

internal static class TestGranularityResolver
{
    internal const string OverrideVariable = "RAUN_TEST_GRANULARITY";

    /// <summary>Set by Stryker.NET's Microsoft.Testing.Platform runner on every test host it starts.</summary>
    internal const string StrykerVariable = "STRYKER_MUTANT_FILE";

    public static TestGranularity Resolve(TestGranularity? configured, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var requested = environment(OverrideVariable)?.Trim();
        if (!string.IsNullOrEmpty(requested))
        {
            if (requested.Equals("step", StringComparison.OrdinalIgnoreCase))
            {
                return TestGranularity.Step;
            }

            if (requested.Equals("scenario", StringComparison.OrdinalIgnoreCase))
            {
                return TestGranularity.Scenario;
            }

            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Environment variable {0} is '{1}'; it must be 'step' or 'scenario'.",
                OverrideVariable,
                requested));
        }

        if (configured is { } chosen)
        {
            return chosen;
        }

        return string.IsNullOrWhiteSpace(environment(StrykerVariable))
            ? TestGranularity.Step
            : TestGranularity.Scenario;
    }
}
