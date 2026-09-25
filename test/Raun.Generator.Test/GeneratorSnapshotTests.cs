using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Raun.Testing;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Snapshots of the generated source so the lowering output stays easy to review and changes are
/// caught. Behavioral correctness is covered by the *LoweringTests; these guard the exact shape.
/// One snapshot file per generated file, named <c>&lt;test&gt;#&lt;hint name&gt;.verified.cs</c>, so
/// the split into a file per scenario is itself visible in the review.
/// </summary>
public class GeneratorSnapshotTests
{
    private const string Directory = "Snapshots";

    [Fact]
    public void Linear_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.LinearScenario));

    [Fact]
    public void Tuple_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.TupleScenario));

    [Fact]
    public void Array_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.ArrayScenario));

    /// <summary>Pins the runtime display-name formatter's shape: a string concatenation of literals
    /// and parenthesized <c>__inputs.Get&lt;…&gt;(i)</c> holes, not a hand-escaped interpolation.</summary>
    [Fact]
    public void RuntimeName_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.RuntimeNameScenario));

    [Fact]
    public void PathBearing_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.LinearScenario, "Scenario.cs"));

    [Fact]
    public void EntryPoint() =>
        Snapshot.Verify(
            GeneratorHarness.RunGeneratedFiles(SampleSources.Dsl + SampleSources.LinearScenario)["RaunProgram.g.cs"],
            directory: Directory);

    [Fact]
    public void Conditional_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.ConditionalDsl + SampleSources.IfElseScenario));

    [Fact]
    public void Resource_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.ResourceDsl + SampleSources.ResourceScenario));

    [Fact]
    public void Uses_scenario() =>
        VerifyDriver(GeneratorHarness.RunDriver(SampleSources.UsesDsl + SampleSources.UsesScenario));

    /// <summary>
    /// Snapshots every file the driver generated, one snapshot per hint name, and fails when a
    /// verified file exists that this run did not produce — without that, deleting a generated file
    /// would leave its snapshot behind, passing, for ever.
    /// </summary>
    private static void VerifyDriver(GeneratorDriver driver, [CallerMemberName] string testName = "")
    {
        var sources = driver.GetRunResult().Results
            .SelectMany(result => result.GeneratedSources)
            .OrderBy(source => source.HintName, System.StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(sources);

        var written = new List<string>();
        foreach (var source in sources)
        {
            // Verified files carry the hint name and open with it, so a file's identity is visible
            // both in the directory listing and at the top of the diff.
            var hint = source.HintName;
            written.Add(Snapshot.Verify(
                "//HintName: " + hint + "\n" + source.SourceText,
                extension: "cs",
                directory: Directory,
                nameSuffix: "#" + Path.GetFileNameWithoutExtension(hint),
                testName: testName));
        }

        var expected = written.Select(Path.GetFullPath).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        var folder = Path.GetDirectoryName(written[0])!;
        var stale = System.IO.Directory
            .EnumerateFiles(folder, $"*.{testName}#*.verified.cs")
            .Where(path => !expected.Contains(Path.GetFullPath(path)))
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{testName} no longer generates: {string.Join(", ", stale.Select(Path.GetFileName))}. "
            + "Delete the snapshot if that is intended.");
    }
}
