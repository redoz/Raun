using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Raun.Coverage.Coverlet;
using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverletConditionalExecutionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Compiled_same_line_ternary_maps_arms_by_call_identity_not_path_number(bool release, bool negate)
    {
        var source = "class C { static int A() => 1; static int B() => 2; public static int M(bool b) => " + (negate ? "!b" : "b") + " ? A() : B(); }";
        using var fixture = new Fixture(source, release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decision = Assert.Single(method.Instructions, instruction => instruction.IsConditional);
        var fallthrough = method.Instructions.Single(instruction => instruction.Offset == decision.NextOffset);
        var target = method.Instructions.Single(instruction => instruction.Offset == decision.Target);
        // Fixture arms each begin with a distinct zero-argument call. Recorded path labels
        // are deliberately reversed; only actual IL destinations identify the source arm.
        Assert.NotNull(fallthrough.Call);
        Assert.NotNull(target.Call);
        var paths = new[]
        {
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 73, fallthrough.Call!.Contains("::A/", StringComparison.Ordinal) ? 0 : 4) { EndOffset = fallthrough.Offset },
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 2, target.Call!.Contains("::A/", StringComparison.Ordinal) ? 0 : 4) { EndOffset = target.Offset },
        };
        var module = fixture.Module with { Paths = paths };
        var ranges = CoverletConditionalExecution.Build(fixture.Branches, [module]);
        var conditional = Assert.Single(fixture.Branches).Syntax;
        Assert.Equal(CoverageHit.No, Hit("A()"));
        Assert.Equal(CoverageHit.Yes, Hit("B()"));
        var condition = ((Microsoft.CodeAnalysis.CSharp.Syntax.ConditionalExpressionSyntax)conditional).Condition;
        Assert.Contains(ranges, range => range.Start == condition.GetLocation().GetLineSpan().StartLinePosition && range.Hit == CoverageHit.Yes);
        CoverageHit Hit(string arm)
        {
            var syntax = conditional.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().Single(node => node.ToString() == arm);
            return Assert.Single(ranges, range => range.Start == syntax.GetLocation().GetLineSpan().StartLinePosition && range.End == syntax.GetLocation().GetLineSpan().EndLinePosition).Hit;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_call_on_both_arms_stays_unmapped(bool release)
    {
        using var fixture = new Fixture("class C { static int A(int x) => x; public static int M(bool b) => b ? A(1) : A(2); }", release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decision = Assert.Single(method.Instructions, instruction => instruction.IsConditional);
        var module = fixture.Module with { Paths = [
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 0, 3) { EndOffset = decision.NextOffset },
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 1, 0) { EndOffset = decision.Target }] };
        Assert.Empty(CoverletConditionalExecution.Build(fixture.Branches, [module]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Nested_multiline_ternaries_share_enclosing_point_but_keep_distinct_arm_evidence(bool release, bool skipNested)
    {
        const string source = """
        class C {
            static int A() => 1; static int B() => 2; static int D() => 3;
            public static int M(bool a, bool b) =>
                a
                    ? D()
                    : b
                        ? A()
                        : B();
        }
        """;
        using var fixture = new Fixture(source, release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decisions = method.Instructions.Where(instruction => instruction.IsConditional).ToArray();
        Assert.Equal(2, decisions.Length);
        var paths = new List<CoveragePath>();
        foreach (var decision in decisions)
        {
            foreach (var end in new[] { decision.NextOffset, decision.Target })
            {
                // Walk the straight successor prefix; the outer nested arm stops at the
                // inner decision before any call, while the other three begin leaf calls.
                var instructions = method.Instructions.Where(instruction => instruction.Offset >= end).TakeWhile(instruction => !instruction.IsConditional).ToArray();
                var call = instructions.FirstOrDefault(instruction => instruction.Call is not null)?.Call;
                var hits = decision == decisions[0] ? call?.Contains("::D/", StringComparison.Ordinal) == true ? skipNested ? 1 : 0 : skipNested ? 0 : 1
                    : !skipNested && call?.Contains("::B/", StringComparison.Ordinal) == true ? 1 : 0;
                paths.Add(new CoveragePath("fixture.cs", "folded", 4, decision.Offset, paths.Count, hits) { EndOffset = end });
            }
        }
        var ranges = CoverletConditionalExecution.Build(fixture.Branches, [fixture.Module with { Paths = paths }]);
        Assert.Equal(skipNested ? 5 : 6, ranges.Length);
        var root = fixture.Branches[0].Syntax;
        CoverageHit Hit(string text) => Assert.Single(ranges, range =>
        {
            var node = root.DescendantNodesAndSelf().Single(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax && node.ToString() == text);
            var location = node.GetLocation().GetLineSpan();
            return range.Start == location.StartLinePosition && range.End == location.EndLinePosition;
        }).Hit;
        Assert.Equal(skipNested ? CoverageHit.Yes : CoverageHit.No, Hit("D()"));
        Assert.Equal(CoverageHit.No, Hit("A()"));
        Assert.Equal(skipNested ? CoverageHit.No : CoverageHit.Yes, Hit("B()"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_continuation_calls_cannot_supply_an_arm_marker(bool release)
    {
        using var fixture = new Fixture("class C { static int A(int x) => x; public static int M(bool b) { var x = b ? 1 : 2; return A(x); } }", release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decision = Assert.Single(method.Instructions, instruction => instruction.IsConditional);
        var module = fixture.Module with { Paths = [
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 0, 1) { EndOffset = decision.NextOffset },
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 1, 0) { EndOffset = decision.Target }] };
        Assert.Empty(CoverletConditionalExecution.Build(fixture.Branches, [module]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Constant_ternary_cannot_borrow_another_decisions_counters(bool release, bool nested)
    {
        var source = nested
            ? "class C { static int A() => 1; static int B() => 2; public static int M(bool b) => b ? (true ? A() : B()) : B(); }"
            : "class C { static int A() => 1; static int B() => 2; public static int M(bool b) { if (b) A(); else B(); return true ? A() : B(); } }";
        using var fixture = new Fixture(source, release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decision = Assert.Single(method.Instructions, instruction => instruction.IsConditional);
        var module = fixture.Module with { Paths = [
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 0, 1) { EndOffset = decision.NextOffset },
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 1, 0) { EndOffset = decision.Target }] };
        Assert.Empty(CoverletConditionalExecution.Build(fixture.Branches, [module]));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Folded_same_line_lambda_records_with_duplicate_IL_signatures_stay_unmapped(bool release)
    {
        using var fixture = new Fixture("class C { static int A() => 1; static int B() => 2; public static object M() { System.Func<bool,int> x = b => b ? A() : B(), y = b => b ? A() : B(); return new[] { x, y }; } }", release);
        var methods = fixture.Module.Methods.Where(method => method.Name.Contains("b__", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, methods.Length);
        var decision = Assert.Single(methods[0].Instructions, instruction => instruction.IsConditional);
        Assert.Contains(methods[1].Instructions, instruction => instruction.Offset == decision.Offset && instruction.NextOffset == decision.NextOffset && instruction.Target == decision.Target);
        var module = fixture.Module with { Paths = [
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 0, 1) { EndOffset = decision.NextOffset },
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 1, 0) { EndOffset = decision.Target }] };
        Assert.Empty(CoverletConditionalExecution.Build(fixture.Branches, [module]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Earlier_if_counters_are_outside_later_ternary_sequence_point(bool release)
    {
        using var fixture = new Fixture("class C { static int A() => 1; static int B() => 2; public static int M(bool b, bool c) { if (b) A(); else B(); return c ? A() : B(); } }", release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decisions = method.Instructions.Where(instruction => instruction.IsConditional).ToArray();
        Assert.Equal(2, decisions.Length);
        var module = fixture.Module with { Paths = [
            new CoveragePath("fixture.cs", "folded", 1, decisions[0].Offset, 0, 1) { EndOffset = decisions[0].NextOffset },
            new CoveragePath("fixture.cs", "folded", 1, decisions[0].Offset, 1, 0) { EndOffset = decisions[0].Target }] };
        Assert.Empty(CoverletConditionalExecution.Build(fixture.Branches, [module]));
    }
    [Theory]
    [InlineData(false, "c || true")]
    [InlineData(true, "c || true")]
    [InlineData(false, "c && false")]
    [InlineData(true, "c && false")]
    public void Folded_compound_condition_cannot_borrow_parent_branch_counters(bool release, string condition)
    {
        using var fixture = new Fixture("class C { static int A() => 1; static int B() => 2; public static int M(bool b, bool c) => b ? ((" + condition + ") ? A() : B()) : B(); }", release);
        var method = Assert.Single(fixture.Module.Methods, method => method.Name == "M");
        var decision = method.Instructions.First(instruction => instruction.IsConditional);
        var module = fixture.Module with { Paths = [
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 0, 1) { EndOffset = decision.NextOffset },
            new CoveragePath("fixture.cs", "folded", 1, decision.Offset, 1, 0) { EndOffset = decision.Target }] };
        Assert.Empty(CoverletConditionalExecution.Build(fixture.Branches, [module]));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "raun-ternary-" + Guid.NewGuid().ToString("N"));
        public CoverageModule Module { get; }
        public IReadOnlyList<InventoryBranch> Branches { get; }
        public Fixture(string source, bool release)
        {
            Directory.CreateDirectory(_directory);
            var tree = CSharpSyntaxTree.ParseText(source, path: "fixture.cs", encoding: new UTF8Encoding(false));
            var compilation = TestCompilation.Create(source).RemoveAllSyntaxTrees().AddSyntaxTrees(tree)
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: release ? OptimizationLevel.Release : OptimizationLevel.Debug));
            var output = Path.Combine(_directory, "Fixture.dll");
            using (var assembly = File.Create(output))
            using (var pdb = File.Create(Path.ChangeExtension(output, ".pdb")))
            { Assert.True(compilation.Emit(assembly, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)).Success); }
            Module = CoverletPdbMapper.Map(new CoverageModule("", "Fixture.dll", []) { IsCoverlet = true, Lines = Enumerable.Range(1, tree.GetText().Lines.Count).Select(line => new CoverageLine("fixture.cs", line, 4)).ToArray() }, output);
            Branches = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        }
        public void Dispose() { Directory.Delete(_directory, true); }
    }
}
