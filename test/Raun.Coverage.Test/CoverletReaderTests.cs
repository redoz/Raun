using Raun.Reporting.Coverage;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Raun.Coverage.Inventory;
using Raun.Coverage.Projects;
using Raun.Coverage.Coverlet;
using Raun.Coverage.MicrosoftXml;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverletReaderTests
{
    [Fact]
    public void Collector_total_counts_recorded_paths_instead_of_hit_frequency()
    {
        var observations = new CoverageObservations([
            new CoverageModule("", "Api.dll", [])
            {
                IsCoverlet = true,
                Paths = [new CoveragePath("a.cs", "M", 1, 0, 0, 3917), new CoveragePath("a.cs", "M", 1, 0, 1, 0)],
            },
            new CoverageModule("", "Other.dll", [])
            {
                IsCoverlet = true,
                Paths = [new CoveragePath("b.cs", "M", 1, 0, 0, 2), new CoveragePath("b.cs", "M", 1, 0, 1, 0)],
            },
        ]);
        var counts = ClassifiedCoverageAnalyzer.SummarizeBranchPaths(observations);
        Assert.NotNull(counts);
        Assert.Equal(2, counts.Covered);
        Assert.Equal(4, counts.Total);
    }

    [Fact]
    public void Collector_total_stays_unavailable_without_branch_path_data()
    {
        Assert.Null(ClassifiedCoverageAnalyzer.SummarizeBranchPaths(new CoverageObservations([new CoverageModule("", "Api.dll", [])])));
        var counts = ClassifiedCoverageAnalyzer.SummarizeBranchPaths(new CoverageObservations([new CoverageModule("", "Api.dll", []) { IsCoverlet = true }]));
        Assert.NotNull(counts);
        Assert.Equal(0, counts.Total);
    }

    [Fact]
    public void Reader_retains_path_counters_without_inventing_arm_names()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
        {"Fixture.dll":{"fixture.cs":{"C":{"M":{"Lines":{"31":2,"32":0},"Branches":[
          {"Line":31,"Offset":22,"EndOffset":40,"Path":0,"Ordinal":0,"Hits":1},
          {"Line":31,"Offset":22,"EndOffset":60,"Path":1,"Ordinal":1,"Hits":1}]}}}}}
        """));
        var module = Assert.Single(CoverletCoverageJsonReader.Read(stream).Modules);
        Assert.Equal("Fixture.dll", module.Name);
        Assert.Equal(2, module.Lines.Count);
        Assert.Equal(new long[] { 1, 1 }, module.Paths.Select(path => path.Hits));
        Assert.Empty(module.Ranges);
        Assert.True(module.IsCoverlet);
    }

    [Fact]
    public void Pdb_mapping_marks_shared_positive_lines_partial_and_zero_lines_missed()
    {
        const string source = "class C { int M(bool b) { if (b) return 1; return 2; } }";
        var compilation = TestCompilation.Create(source);
        var encoded = CSharpSyntaxTree.ParseText(source, path: "fixture.cs", encoding: new UTF8Encoding(false));
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(encoded);
        var directory = Path.Combine(Path.GetTempPath(), "raun-pdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "Fixture.dll");
            using (var assembly = File.Create(output))
            using (var pdb = File.Create(Path.ChangeExtension(output, ".pdb")))
            { Assert.True(compilation.Emit(assembly, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)).Success); }
            var module = new CoverageModule("", "Fixture.dll", []) { IsCoverlet = true, Lines = [new CoverageLine("fixture.cs", 1, 5)] };
            var mapped = CoverletPdbMapper.Map(module, output);
            Assert.NotEmpty(mapped.Ranges);
            Assert.All(mapped.Ranges, range => Assert.Equal(CoverageHit.Partial, range.Hit));
            Assert.True(ModuleIdentity.Matches(output, mapped));
            var zero = CoverletPdbMapper.Map(module with { Lines = [new CoverageLine("fixture.cs", 1, 0)] }, output);
            Assert.All(zero.Ranges, range => Assert.Equal(CoverageHit.No, range.Hit));
            var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken));
            Assert.True(CoverageResolver.VerifySource(branch, mapped.Ranges[0].Source));
            Assert.False(CoverageResolver.VerifySource(branch, mapped.Ranges[0].Source with { Checksum = "bad" }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("class C { int M(bool b) { return b ? 1 : 2; } }", true)]
    [InlineData("class C { int M(bool a, bool b) { return a ? (b ? 1 : 2) : 3; } }", false)]
    [InlineData("class C { int M(bool b) { while (b) { if (true) return 1; } return 2; } }", false)]
    public void Counter_groups_require_one_source_decision_and_one_lowered_offset_group(string source, bool unique)
    {
        var branch = BranchInventoryBuilder.Build(TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken)[0];
        var module = new CoverageModule("build", "Fixture.dll", []) { IsCoverlet = true,
            Paths = [new CoveragePath("fixture.cs", "M", 1, 20, 0, 4), new CoveragePath("fixture.cs", "M", 1, 20, 1, 0)] };
        Assert.Equal(unique ? new long[] { 4, 0 } : [], CoverletBranchEvidence.PathHits(branch, [module]));
        Assert.Empty(CoverletBranchEvidence.PathHits(branch, [module with { Paths = [.. module.Paths, new CoveragePath("fixture.cs", "lambda", 1, 20, 0, 1)] }]));
    }

    private const string DecisionSource = "class C { int M(bool b) { if (b) return 1; return 2; } }";
    private const string ExpressionSource = "class C { int M(bool b) { return b ? 1 : 2; } }";
    private const string GuardSource = "class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }";

    [Theory]
    [InlineData(DecisionSource, 4L, 0L, (int)EvidenceStatus.Incomplete)]
    [InlineData(DecisionSource, 1L, 1L, (int)EvidenceStatus.Complete)]
    [InlineData(DecisionSource, 0L, 0L, (int)EvidenceStatus.NotReached)]
    [InlineData(ExpressionSource, 4L, 0L, (int)EvidenceStatus.Incomplete)]
    [InlineData(ExpressionSource, 1L, 1L, (int)EvidenceStatus.Complete)]
    [InlineData(ExpressionSource, 0L, 0L, (int)EvidenceStatus.NotReached)]
    [InlineData(GuardSource, 4L, 0L, (int)EvidenceStatus.Incomplete)]
    [InlineData(GuardSource, 1L, 1L, (int)EvidenceStatus.Complete)]
    [InlineData(GuardSource, 0L, 0L, (int)EvidenceStatus.NotReached)]
    public void Unique_decision_paths_resolve_without_assigning_true_false_labels(string source, long first, long second, int expectedValue)
    {
        var expected = (EvidenceStatus)expectedValue;
        var branch = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken));
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        var document = new CoverageSource("fixture.cs", "SHA256", checksum);
        var location = branch.Syntax.GetLocation().GetLineSpan();
        var module = new CoverageModule("build", "Fixture.dll", [new CoverageRange(document, location.StartLinePosition, location.EndLinePosition, CoverageHit.Partial)])
        {
            IsCoverlet = true,
            Paths = [new CoveragePath("fixture.cs", "M", 1, 20, 0, first), new CoveragePath("fixture.cs", "M", 1, 20, 1, second)],
        };
        Assert.Equal(expected, CoverageResolver.Resolve(branch, new CoverageObservations([module])));
        var mismatch = module with { Ranges = [module.Ranges[0] with { Source = document with { Checksum = "bad" } }] };
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(branch, new CoverageObservations([mismatch])));
        var snippets = CoverageSourceBuilder.Build([(branch, BranchCategory.Logic, expected)], new CoverageObservations([module]));
        Assert.Equal(new long[] { first, second }, Assert.Single(Assert.Single(snippets).Branches).PathHits);
    }

    [Fact]
    public void Coverlet_scope_honors_module_type_source_and_attribute_opt_outs()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
        {"Coverlet":{"Include":"[Fixture]*", "Exclude":"[Fixture]Ignored*", "ExcludeByFile":"**/Excluded.cs", "ExcludeByAttribute":"MyIgnore"}}
        """));
        var scope = CoverletSettingsReader.Read(stream);
        Assert.True(scope.IncludesCoverletType("Fixture", "C"));
        Assert.False(scope.IncludesCoverletType("Fixture", "IgnoredClass"));
        Assert.False(scope.IncludesCoverletType("Other", "C"));
        Assert.False(scope.IncludesSource("src/Excluded.cs"));
        Assert.False(scope.IncludesSource(@"C:\src\Excluded.cs"));
        Assert.True(scope.IncludesSource(@"C:\src\Excluded.cs.generated.cs"));
        var fileScope = new CollectorScope { CoverletSourceExcludes = ["**/Generated/*.cs"] };
        Assert.False(fileScope.IncludesSource(@"C:\src\Generated\One.cs"));
        Assert.True(fileScope.IncludesSource(@"C:\src\Generated\Nested\One.cs"));
        var compilation = TestCompilation.Create("class MyIgnoreAttribute : System.Attribute {} class C { [MyIgnore] int M(bool b) => b ? 1 : 2; }");
        Assert.Empty(BranchInventoryBuilder.Build(compilation, scope, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("**/Excluded.cs", @"src\Excluded.cs", false)]
    [InlineData(@"**\Excluded.cs", "src/Excluded.cs", false)]
    [InlineData(@"**\Excluded.cs", @"src\Excluded.cs", false)]
    [InlineData(@"**\Excluded.cs", "src/Excluded.cs.generated.cs", true)]
    [InlineData(@"**\Generated\*.cs", "src/Generated/One.cs", false)]
    [InlineData("**/Generated/*.cs", @"src/Generated\One.cs", false)]
    [InlineData(@"**/Generated\*.cs", @"src\Generated/One.cs", false)]
    [InlineData(@"**\Generated\*.cs", @"src\Generated\Nested\One.cs", true)]
    [InlineData(@"\src\Generated\*.cs", "/src/Generated/One.cs", false)]
    [InlineData("/src/Generated/*.cs", @"\src\Generated\One.cs", false)]
    public void Coverlet_source_exclusions_normalize_both_glob_and_source_separators(string pattern, string source, bool included)
    {
        var scope = new CollectorScope { CoverletSourceExcludes = [pattern] };
        Assert.Equal(included, scope.IncludesSource(source));
    }
    [Fact]
    public void Effective_settings_choose_specific_testconfig_then_cli_overrides()
    {
        var directory = Path.Combine(Path.GetTempPath(), "raun-coverlet-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "coverlet.mtp.appsettings.json"), """{"Coverlet":{"Include":"[Legacy]*"}}""");
            File.WriteAllText(Path.Combine(directory, "testconfig.json"), """{"platformOptions":{"coverlet":{"include":"[General]*"}}}""");
            File.WriteAllText(Path.Combine(directory, "Fixture.testconfig.json"), """{"platformOptions":{"coverlet":{"include":"[Fixture]*", "exclude":"[Fixture]FileExcluded", "excludeByFile":"**/File.cs", "excludeByAttribute":"FileIgnored", "singleHit":true}}}""");
            var output = Path.Combine(directory, "Fixture.dll");
            var fileScope = CoverletSettingsReader.Read(output, directory, _ => null, _ => false);
            Assert.True(fileScope.IncludesCoverletType("Fixture", "Included"));
            Assert.False(fileScope.IncludesCoverletType("General", "Included"));
            Assert.True(fileScope.CoverletSingleHit);
            Assert.False(fileScope.IncludesSource(@"C:\src\File.cs"));
            var arguments = new Dictionary<string, string[]>
            {
                ["coverlet-include"] = ["[Cli]*"], ["coverlet-exclude"] = ["[Cli]CliExcluded"],
                ["coverlet-exclude-by-file"] = ["**/Cli.cs"], ["coverlet-exclude-by-attribute"] = ["CliIgnored"],
            };
            var cli = CoverletSettingsReader.Read(output, directory, option => arguments.GetValueOrDefault(option), option => option == "coverlet-include-test-assembly");
            Assert.True(cli.IncludeTestAssembly);
            Assert.True(cli.IncludesCoverletType("Cli", "Included"));
            Assert.False(cli.IncludesCoverletType("Fixture", "Included"));
            Assert.False(cli.IncludesCoverletType("Cli", "CliExcluded"));
            Assert.True(cli.IncludesSource(@"C:\src\File.cs"));
            Assert.False(cli.IncludesSource(@"C:\src\Cli.cs"));
            var compilation = TestCompilation.Create("class CliIgnoredAttribute : System.Attribute {} class C { [CliIgnored] int M(bool b) => b ? 1 : 2; }").WithAssemblyName("Cli");
            Assert.Empty(BranchInventoryBuilder.Build(compilation, cli, TestContext.Current.CancellationToken));
            var optional = new CollectorScope { CoverletIncludes = ["[*.tests?]*"] };
            Assert.True(optional.IncludesCoverletType("Foo.test", "C"));
            Assert.True(optional.IncludesCoverletType("Foo.tests", "C"));
            Assert.False(optional.IncludesCoverletType("Foo.testX", "C"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Single_hit_mode_does_not_surface_frequency_counters()
    {
        const string source = "class C { int M(bool b) { return b ? 1 : 2; } }";
        var branch = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken));
        var location = branch.Syntax.GetLocation().GetLineSpan();
        var document = new CoverageSource("fixture.cs", "SHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        var module = new CoverageModule("build", "Fixture.dll", [new CoverageRange(document, location.StartLinePosition, location.EndLinePosition, CoverageHit.Partial)])
        {
            IsCoverlet = true, HasFrequencyCounts = false,
            Paths = [new CoveragePath("fixture.cs", "M", 1, 20, 0, 1), new CoveragePath("fixture.cs", "M", 1, 20, 1, 1)],
        };
        var snippets = CoverageSourceBuilder.Build([(branch, BranchCategory.Logic, EvidenceStatus.Complete)], new CoverageObservations([module]));
        Assert.Empty(Assert.Single(Assert.Single(snippets).Branches).PathHits);
        Assert.Equal(EvidenceStatus.Complete, CoverageResolver.Resolve(branch, new CoverageObservations([module])));
    }

    [Fact]
    public void Executed_statement_does_not_color_both_conditional_operands_green()
    {
        const string source = "class C { int M(bool b) { return b ? 1 : 2; } }";
        var branch = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken));
        var span = branch.Syntax.GetLocation().GetLineSpan();
        var document = new CoverageSource("fixture.cs", "SHA256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        var module = new CoverageModule("build", "Fixture.dll", [new CoverageRange(document, span.StartLinePosition, span.EndLinePosition, CoverageHit.Yes)]) { IsCoverlet = true };
        var snippet = Assert.Single(CoverageSourceBuilder.Build([(branch, BranchCategory.Logic, EvidenceStatus.Reached)], new CoverageObservations([module])));
        Assert.Equal("partial", Assert.Single(snippet.Execution).State);
    }

    [Fact]
    public void Negative_counts_and_non_coverlet_documents_are_rejected()
    {
        using var negative = new MemoryStream(Encoding.UTF8.GetBytes("""
        {"Fixture.dll":{"fixture.cs":{"C":{"M":{"Lines":{"31":-1},"Branches":[]}}}}}
        """));
        Assert.Throws<InvalidDataException>(() => CoverletCoverageJsonReader.Read(negative));
        using var unrelated = new MemoryStream(Encoding.UTF8.GetBytes("{\"coverage\":42}"));
        Assert.Throws<InvalidDataException>(() => CoverletCoverageJsonReader.Read(unrelated));
    }
}
