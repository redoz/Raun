using Xunit;

namespace Raun.Generator.Test;

public sealed class SourceLocationTests
{
    [Fact]
    public void A_scenario_records_the_enclosing_class_declaration_as_well_as_the_method()
    {
        var source = SampleSources.Dsl + """

            public sealed class SourceLinkScenarios : AppointmentSuite
            {
                [Scenario("source link")]
                public async Task Run() { await Given.DatabaseIsClean(); }
            }
            """;
        var result = GeneratorHarness.RunDriver(source, path: "/repo/tests/Bookings.cs").GetRunResult();
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var generated = string.Join("\n", result.GeneratedTrees.Select(tree => tree.ToString()));
        var classLine = source[..source.IndexOf("public sealed class SourceLinkScenarios", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        Assert.Contains("TypeSourceFile = \"/repo/tests/Bookings.cs\"", generated, StringComparison.Ordinal);
        Assert.Contains("TypeSourceLine = " + classLine.ToString(System.Globalization.CultureInfo.InvariantCulture), generated, StringComparison.Ordinal);
    }
}
