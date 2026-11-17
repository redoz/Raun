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

/// <summary>
/// Decides the <see cref="TestGranularity"/> for a test host. Order: the per-run override
/// (<see cref="OverrideVariable"/>), then the suite's own choice in code, then Stryker's presence
/// (<see cref="StrykerVariable"/>), then <see cref="TestGranularity.Step"/>.
/// </summary>
/// <remarks>
/// The per-run override is an environment variable rather than a command-line option because
/// Stryker passes no arguments of the user's to the test host it starts, but does hand down its
/// environment. The value must not change during the life of a host: a tool that maps tests by uid
/// across its discovery and run requests breaks if the uids change between them.
/// </remarks>
internal static class TestGranularityResolver
{
    /// <summary>Per-run override: <c>step</c> or <c>scenario</c>, case-insensitive.</summary>
    internal const string OverrideVariable = "RAUN_TEST_GRANULARITY";

    /// <summary>Set by Stryker.NET's Microsoft.Testing.Platform runner on every test host it starts.</summary>
    internal const string StrykerVariable = "STRYKER_MUTANT_FILE";

    /// <summary>Resolves the granularity from <paramref name="configured"/> (the suite's choice in
    /// code, <see langword="null"/> for automatic) and an environment lookup.</summary>
    /// <exception cref="InvalidOperationException">The override variable holds an unknown value.</exception>
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
