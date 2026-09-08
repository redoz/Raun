using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A scenario the parser cannot lower is reported (RAUN017) at the offending statement, never
/// silently dropped. The analyzer usually says the same thing more precisely; this is the safety net
/// for the day the two disagree, because a scenario that vanishes from the test list fails no test.
/// </summary>
public class RejectionReportingTests
{
    private const string RejectedId = "RAUN017";

    private const string TopLevelRejection =
        """

        public static class RejectScenarios
        {
            [Scenario("rejected")]
            public static async Task Run()
            {
                var jane = await Given.PatientExists("Jane");
                var n = 1;
                await Then.Greet(jane);
            }
        }
        """;

    private const string NestedRejection =
        """

        public static class NestedRejectScenarios
        {
            [Scenario("nested rejected")]
            public static async Task Run()
            {
                var patient = await Given.PatientExists("Jane");
                if (await Given.IsPriority())
                {
                    var n = 1;
                    await When.Notify(patient);
                }
            }
        }
        """;

    private const string ExpressionBodied =
        """

        public static class ExpressionBodiedScenarios
        {
            [Scenario("expression bodied")]
            public static Task Run() => Task.CompletedTask;
        }
        """;

    [Fact]
    public void An_unsupported_top_level_statement_is_reported_at_that_statement()
    {
        var source = SampleSources.Dsl + TopLevelRejection;

        var result = GeneratorHarness.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == RejectedId);
        Assert.Equal(LineOf(source, "var n = 1;"), diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains("RejectScenarios.Run", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain("rejected", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejection_inside_an_if_arm_points_at_the_inner_statement()
    {
        var source = SampleSources.ConditionalDsl + NestedRejection;

        var result = GeneratorHarness.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == RejectedId);
        Assert.Equal(LineOf(source, "var n = 1;"), diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void An_expression_bodied_scenario_is_reported_at_its_name()
    {
        var source = SampleSources.Dsl + ExpressionBodied;

        var result = GeneratorHarness.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == RejectedId);
        Assert.Equal(LineOf(source, "public static Task Run() =>"), diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void A_rejected_scenario_does_not_hide_its_compilable_neighbours()
    {
        var source = SampleSources.Dsl + SampleSources.LinearScenario + TopLevelRejection;

        var result = GeneratorHarness.Run(source);

        Assert.Single(result.GeneratorDiagnostics, d => d.Id == RejectedId);
        Assert.Single(result.Definitions());
    }

    /// <summary>0-based line index of the first line containing <paramref name="needle"/>, matching
    /// Roslyn's <c>LinePosition.Line</c>.</summary>
    private static int LineOf(string source, string needle)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("needle not found: " + needle);
    }
}
