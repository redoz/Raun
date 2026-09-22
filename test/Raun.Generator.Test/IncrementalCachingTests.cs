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
    private const string Source = """
        using System.Threading.Tasks;
        using Raun;

        namespace Demo;

        public static class Given
        {
            [StepName("Given patient {name} exists")]
            public static Task<string> PatientExists(string name) => Task.FromResult(name);
        }

        public static class When
        {
            [StepName("When booking for {patient}")]
            public static Task<int> Book(string patient) => Task.FromResult(1);
        }

        public static class Scenarios
        {
            [Scenario("a patient books")]
            public static async Task Books()
            {
                var patient = await Given.PatientExists("Jane");
                await When.Book(patient);
            }

            [Scenario("another patient books")]
            public static async Task BooksAgain()
            {
                var patient = await Given.PatientExists("John");
                await When.Book(patient);
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

    /// <summary>Runs the generator over <paramref name="first"/>, then again over
    /// <paramref name="second"/> parsed as a fresh tree, and returns the run reasons of the second
    /// run's source-output steps.</summary>
    private static ImmutableArray<IncrementalStepRunReason> RunTwice(string first, string second)
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

        return
        [
            .. result.TrackedOutputSteps
                .SelectMany(static kvp => kvp.Value)
                .SelectMany(static step => step.Outputs)
                .Select(static output => output.Reason),
        ];
    }
}
