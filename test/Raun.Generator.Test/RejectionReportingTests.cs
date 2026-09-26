using System.Globalization;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A scenario the parser cannot lower is reported where the problem is written — under the rule that
/// names it, or RAUN017 with a reason when none does — and never silently dropped: a scenario is
/// generated or reported, because the parser's outcome is one or the other. Every problem in a body is
/// reported in the same build, and one failed statement does not drag its dependents down with it.
/// </summary>
public class RejectionReportingTests
{
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
            public static async Task Run() => await Given.DatabaseIsClean();
        }
        """;

    [Fact]
    public async Task An_unsupported_statement_is_reported_at_that_statement()
    {
        var source = SampleSources.Dsl + TopLevelRejection;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN002", diagnostic.Id);
        Assert.Equal(LineOf(source, "var n = 1;"), diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task A_problem_inside_an_if_arm_points_at_the_inner_statement()
    {
        var source = SampleSources.ConditionalDsl + NestedRejection;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN002", diagnostic.Id);
        Assert.Equal(LineOf(source, "var n = 1;"), diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task An_expression_bodied_scenario_is_refused_at_its_name_with_the_reason()
    {
        var source = SampleSources.Dsl + ExpressionBodied;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN017", diagnostic.Id);
        Assert.Equal(LineOf(source, "public static async Task Run() =>"), diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains("ExpressionBodiedScenarios.Run", Message(diagnostic), StringComparison.Ordinal);
        Assert.Contains("must be a block", Message(diagnostic), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_problem_in_a_body_is_reported_in_one_build()
    {
        var source = SampleSources.Dsl +
            """

            public static class ManyProblemsScenarios
            {
                [Scenario("many problems")]
                public static async Task Run()
                {
                    var n = 1;
                    for (var i = 0; i < 2; i++) { }
                    await Given.PatientExists((await Given.PatientExists("x")).Name);
                }
            }
            """;

        var diagnostics = await GeneratorHarness.DiagnoseAsync(source);

        Assert.Equal(["RAUN002", "RAUN003", "RAUN007"], diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_failed_step_does_not_cascade_into_the_steps_that_read_it()
    {
        // `patient` came from a step that could not be lowered; its reader is not reported again for
        // reading "a local no step produced".
        var source = SampleSources.Dsl +
            """

            public static class CascadeScenarios
            {
                [Scenario("cascade")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists(await Given.PatientExists("x") is { } p ? p.Name : "");
                    await Then.Greet(patient);
                }
            }
            """;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN007", diagnostic.Id);
    }

    [Fact]
    public async Task A_failed_step_flowing_into_an_if_merge_is_still_one_diagnostic()
    {
        // `appointment` is unresolved (its step's argument was refused); the if that conditionally
        // overwrites it must not try to merge through it — one RAUN007, never a crash.
        var source = SampleSources.ConditionalDsl +
            """

            public static class UnresolvedMergeScenarios
            {
                [Scenario("unresolved merge")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists("Jane");
                    var appointment = await When.CreateStandard(await Given.PatientExists("x"));
                    if (await Given.IsPriority())
                        appointment = await When.CreateUrgent(patient);
                    await Then.AppointmentExists(appointment);
                }
            }
            """;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN007", diagnostic.Id);
    }

    [Fact]
    public async Task A_failed_declaration_before_an_if_is_one_diagnostic()
    {
        var source = SampleSources.ConditionalDsl +
            """

            public static class FailedDeclarationScenarios
            {
                [Scenario("failed declaration")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists("Jane");
                    Appointment appointment = null!;
                    if (await Given.IsPriority())
                        appointment = await When.CreateUrgent(patient);
                    await Then.AppointmentExists(appointment);
                }
            }
            """;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN002", diagnostic.Id);
    }

    [Fact]
    public async Task A_scenario_diagnostic_is_located_in_source_so_the_editor_can_squiggle_it()
    {
        var source = SampleSources.Dsl + TopLevelRejection;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.True(diagnostic.Location.IsInSource);
    }

    [Fact]
    public void A_rejected_scenario_does_not_hide_its_compilable_neighbours()
    {
        var source = SampleSources.Dsl + SampleSources.LinearScenario + TopLevelRejection;

        var result = GeneratorHarness.Run(source);

        Assert.Single(result.GeneratorDiagnostics, d => d.Id == "RAUN002");
        Assert.Single(result.Definitions());
    }

    private static string Message(Microsoft.CodeAnalysis.Diagnostic diagnostic)
        => diagnostic.GetMessage(CultureInfo.InvariantCulture);

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
