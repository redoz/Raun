using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator has to behave in an editor, where it re-runs on every keystroke. Roslyn caches a
/// pipeline step when its input compares equal to the previous run's, so every value the pipeline
/// carries must have value equality — a Roslyn syntax node does not, and one held in a record field
/// makes that record unequal to its own re-parse. These tests drive the driver twice and assert the
/// emit step was cached, which is the only way to catch a syntax node sneaking back into the IR.
/// </summary>
public class IncrementalCachingTests
{
    /// <summary>Two scenarios over the shared sample DSL, so both a per-scenario file and the
    /// registry are really emitted (a source that lowers to nothing would make these tests vacuous).</summary>
    private const string Source = SampleSources.Dsl + """

        public static class Scenarios
        {
            [Scenario("a patient books")]
            public static async Task Books()
            {
                var patient = await Given.PatientExists("Jane");
                var slot = await Given.AvailableSlot();
                await When.CreateAppointment(patient, slot);
            }

            [Scenario("another patient books")]
            public static async Task BooksAgain()
            {
                var patient = await Given.PatientExists("John");
                var slot = await Given.AvailableSlot();
                await When.CreateAppointment(patient, slot);
            }
        }
        """;

    [Fact]
    public void Re_running_over_an_identical_compilation_caches_the_emit()
    {
        var reasons = RunTwice(Source, Source);

        Assert.All(reasons, r => Assert.Equal(IncrementalStepRunReason.Cached, r));
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void An_edit_that_changes_no_scenario_caches_the_emit()
    {
        // The editor's real case: a keystroke in the file that holds the scenarios. Re-parsing gives
        // a brand-new syntax tree, so anything the pipeline carries by reference looks changed even
        // though the lowered scenarios are identical.
        var reasons = RunTwice(Source, Source + "\n// a comment nobody generates from\n");

        Assert.All(reasons, r => Assert.Equal(IncrementalStepRunReason.Cached, r));
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void Changing_a_scenario_re_runs_the_emit()
    {
        // The guard on the two tests above: caching that never invalidates would pass them too.
        var reasons = RunTwice(Source, Source.Replace("\"Jane\"", "\"Janet\"", StringComparison.Ordinal));

        Assert.Contains(reasons, r => r != IncrementalStepRunReason.Cached);
    }

    [Theory]
    [InlineData(nameof(SampleSources.ResourceScenario))]
    [InlineData(nameof(SampleSources.IfElseScenario))]
    [InlineData(nameof(SampleSources.LinqScenario))]
    [InlineData(nameof(SampleSources.UsesScenario))]
    [InlineData(nameof(SampleSources.TeardownScenario))]
    public void A_comment_edit_caches_the_emit_for_every_lowering_shape(string sampleName)
    {
        var sample = Dsl(sampleName) + (string)typeof(SampleSources).GetField(sampleName)!.GetRawConstantValue()!;
        var reasons = RunTwice(sample, sample + Environment.NewLine + "// a comment nobody generates from" + Environment.NewLine);

        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.Equal(IncrementalStepRunReason.Cached, r));
    }

    private static string Dsl(string sampleName) => sampleName switch
    {
        nameof(SampleSources.ResourceScenario) => SampleSources.ResourceDsl,
        nameof(SampleSources.IfElseScenario) => SampleSources.ConditionalDsl,
        nameof(SampleSources.TeardownScenario) => SampleSources.TeardownDsl,
        nameof(SampleSources.UsesScenario) => SampleSources.UsesDsl,
        _ => SampleSources.Dsl,
    };

    [Fact]
    public void Editing_one_scenario_leaves_the_other_scenario_s_output_alone()
    {
        // Each scenario is emitted into its own file, so an edit re-runs that scenario's emit and
        // nothing else: not its neighbours, and not the registry (whose content is the scenario
        // names, which did not move).
        var edited = Source.Replace("\"John\"", "\"Johnny\"", StringComparison.Ordinal);
        var (reasons, hints) = RunTwiceTracking(Source, edited);

        Assert.Equal(2, hints.Count(h => h.StartsWith("RaunScenario.", StringComparison.Ordinal)));
        Assert.Single(reasons, IncrementalStepRunReason.Modified);
        Assert.True(
            reasons.Count(r => r == IncrementalStepRunReason.Cached) >= 3,
            $"expected the other scenario, the registry and the entry point to be cached; got {string.Join(", ", reasons)}");
    }

    /// <summary>Runs the generator over <paramref name="first"/>, then again over
    /// <paramref name="second"/> parsed as a fresh tree, and returns the run reasons of the second
    /// run's source-output steps.</summary>
    private static ImmutableArray<IncrementalStepRunReason> RunTwice(string first, string second)
        => RunTwiceTracking(first, second).Reasons;

    /// <summary>The second run's source-output run reasons, plus the hint names it produced.</summary>
    private static (ImmutableArray<IncrementalStepRunReason> Reasons, ImmutableArray<string> Hints) RunTwiceTracking(
        string first, string second)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = GeneratorHarness.CompilationFor(first, parseOptions);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ScenarioGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        var updated = compilation
            .RemoveAllSyntaxTrees()
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(second, parseOptions, path: ""));
        driver = driver.RunGenerators(updated);

        var result = Assert.Single(driver.GetRunResult().Results);

        ImmutableArray<IncrementalStepRunReason> reasons =
        [
            .. result.TrackedOutputSteps
                .SelectMany(static kvp => kvp.Value)
                .SelectMany(static step => step.Outputs)
                .Select(static output => output.Reason),
        ];
        ImmutableArray<string> hints = [.. result.GeneratedSources.Select(static source => source.HintName)];
        return (reasons, hints);
    }
}
