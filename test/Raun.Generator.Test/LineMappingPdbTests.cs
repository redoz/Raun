using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Raun.Generator.Test;

public class LineMappingPdbTests(ITestOutputHelper output)
{
    private const string Path = "Scenario.cs";

    [Fact]
    public void Generated_step_call_maps_to_original_invocation_span()
    {
        var source = SampleSources.Dsl + SampleSources.LinearScenario;

        // The exact original span of `When.CreateAppointment(patient, slot)` (1-based).
        var expected = InvocationSpan(source, Path, "When", "CreateAppointment");

        var (errors, pdb) = GeneratorHarness.EmitWithPdb(source, Path);
        Assert.True(errors.IsEmpty, string.Join("; ", errors));

        var visible = GeneratorHarness.ReadSequencePoints(pdb).Where(p => !p.IsHidden).ToList();

        // Calibration aid — read these in the test output while tuning charOffset:
        foreach (var p in visible)
        {
            output.WriteLine($"{p.Document} ({p.StartLine},{p.StartColumn})-({p.EndLine},{p.EndColumn})");
        }

        // (1) Plumbing is hidden / remapped: nothing visible lands in the generated file.
        Assert.All(visible, p => Assert.Equal(Path, p.Document));

        // (2) Column-accurate: a visible point starts exactly at the original call (the user's own
        //     scenario method only produces a point at the *statement* column, never at the call).
        Assert.Contains(visible, p =>
            p.Document == Path && p.StartLine == expected.startLine && p.StartColumn == expected.startCol);
    }

    [Fact]
    public void PathBearing_scenario_compiles()
    {
        var result = GeneratorHarness.RunWithPath(
            SampleSources.Dsl + SampleSources.LinearScenario, "Scenario.cs");
        result.AssertCompiles();
    }

    [Fact]
    public void Resource_step_with_pre_call_claims_still_maps_to_original_invocation_span()
    {
        // When.Suspend([Updated] User) [return: Updated]: the parameter claim is emitted BEFORE the call
        // under `#line hidden`, the return claim after. The call itself must still carry its own
        // span directive, so a breakpoint on the scenario line hits and stepping lands on the user's
        // source — never on generated plumbing.
        var source = SampleSources.ResourceDsl + SampleSources.ResourceScenario;
        var expected = InvocationSpan(source, Path, "When", "Suspend");

        var (errors, pdb) = GeneratorHarness.EmitWithPdb(source, Path);
        Assert.True(errors.IsEmpty, string.Join("; ", errors));

        var visible = GeneratorHarness.ReadSequencePoints(pdb).Where(p => !p.IsHidden).ToList();

        Assert.All(visible, p => Assert.Equal(Path, p.Document));
        Assert.Contains(visible, p =>
            p.StartLine == expected.startLine && p.StartColumn == expected.startCol);
    }

    private static (int startLine, int startCol, int endLine, int endCol) InvocationSpan(
        string source, string path, string receiver, string method)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: path);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: var r },
                Name.Identifier.ValueText: var m,
            } && r == receiver && m == method);
        var s = invocation.GetLocation().GetLineSpan();
        return (s.StartLinePosition.Line + 1, s.StartLinePosition.Character + 1,
                s.EndLinePosition.Line + 1, s.EndLinePosition.Character + 1);   // 1-based
    }
}
