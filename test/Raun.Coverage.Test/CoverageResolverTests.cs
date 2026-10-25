using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverageResolverTests
{
    [Fact]
    public void Both_explicit_arms_require_positive_evidence()
    {
        var branch = Decision("class C { int M(bool b) { if (b) return 1; else return 2; } }");
        Assert.Equal(EvidenceStatus.Complete, Resolve(branch, "yes", "yes"));
        Assert.Equal(EvidenceStatus.Incomplete, Resolve(branch, "yes", "no"));
        Assert.Equal(EvidenceStatus.Unknown, Resolve(branch, "yes", "partial"));
        Assert.Equal(EvidenceStatus.NotReached, Resolve(branch, "no", "no"));
    }

    [Fact]
    public void Fallthrough_false_outcome_and_missing_default_remain_unknown()
    {
        var branch = Decision("class C { int M(bool b) { if (b) System.Console.WriteLine(1); return 2; } }");
        Assert.Equal(EvidenceStatus.Unknown, Resolve(branch, "yes"));
        var selection = Decision("class C { int M(int x) { switch (x) { case 1: return 1; } return 2; } }");
        Assert.Equal(EvidenceStatus.Unknown, Resolve(selection, "yes"));
    }

    [Fact]
    public void Helpers_are_unmeasurable_and_expressions_are_coarse_without_paths()
    {
        var compilation = TestCompilation.Create("using System; class C { string M(string x) { ArgumentNullException.ThrowIfNull(x); if(x.Length == 0) throw new ArgumentException(); return x ?? throw new ArgumentException(); } }");
        var branches = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unmeasurable, Resolve(Assert.Single(branches, b => b.Measurement == MeasurementKind.Helper)));
        Assert.Equal(EvidenceStatus.Unknown, Resolve(Assert.Single(branches, b => b.Measurement == MeasurementKind.Guard), "no"));
        Assert.Equal(EvidenceStatus.Reached, Resolve(Assert.Single(branches, b => b.Measurement == MeasurementKind.Expression), "yes"));
    }

    [Fact]
    public void Mismatched_checksum_and_absent_module_never_count_as_unexecuted()
    {
        var branch = Decision("class C { int M(bool b) { if (b) return 1; else return 2; } }");
        var xml = Xml(branch, "yes", "yes").Replace("checksum=\"", "checksum=\"0", StringComparison.Ordinal);
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(branch, Read(xml)));
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(branch, Read("<results><modules /></results>")));
    }

    [Fact]
    public void Parser_rejects_cobertura_and_external_entities()
    {
        Assert.Throws<InvalidDataException>(() => Read("<coverage />"));
        Assert.Throws<XmlException>(() => Read("<!DOCTYPE results [<!ENTITY x SYSTEM 'file:///secret'>]><results>&x;</results>"));
    }

    [Fact]
    public void Duplicate_observations_union_hits_without_inflating_inventory()
    {
        var branch = Decision("class C { int M(bool b) { if (b) return 1; else return 2; } }");
        var left = Read(Xml(branch, "yes", "no"));
        var right = Read(Xml(branch, "no", "yes"));
        Assert.Equal(EvidenceStatus.Complete, CoverageResolver.Resolve(branch, new CoverageObservations([.. left.Modules, .. right.Modules])));
    }

    [Fact]
    public void Unevaluated_condition_proves_missing_arm_decision_was_not_reached()
    {
        var branch = Decision("class C { int M(bool b) { if (b) System.Console.WriteLine(1); return 2; } }");
        var condition = ((Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)branch.Syntax).Condition.GetLocation().GetLineSpan();
        var probe = branch with { Outcomes = [condition] };
        Assert.Equal(EvidenceStatus.NotReached, CoverageResolver.Resolve(branch, Read(Xml(probe, "no"))));
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(branch, Read(Xml(probe, "yes"))));
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(branch, Read(Xml(probe, "partial"))));
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(branch, Read(Xml(probe, "no").Replace("checksum=\"", "checksum=\"0", StringComparison.Ordinal))));
    }

    [Fact]
    public void Early_return_false_path_uses_the_following_statement()
    {
        var branch = Decision("class C { int M(bool b) { if (b) return 1; return 2; } }");
        Assert.All(branch.Outcomes, outcome => Assert.NotNull(outcome));
        Assert.Equal(EvidenceStatus.Complete, Resolve(branch, "yes", "yes"));
        Assert.Equal(EvidenceStatus.Incomplete, Resolve(branch, "yes", "no"));
        Assert.Equal(EvidenceStatus.Incomplete, Resolve(branch, "no", "yes"));
    }

    [Fact]
    public void Jump_targets_and_fallthrough_do_not_supply_false_path_evidence()
    {
        var branch = Decision("class C { int M(bool b) { if (b) { goto end; } end: return 2; } }");
        Assert.Null(branch.Outcomes[1]);
        Assert.Equal(EvidenceStatus.Unknown, Resolve(branch, "yes"));
    }

    [Fact]
    public void Compound_arm_entry_matches_its_condition_sequence_point()
    {
        var compilation = TestCompilation.Create("class C { int M(bool a, bool b) { if (a) return 1; else if (b) return 2; else return 3; } }");
        var branch = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken)[0];
        var nested = ((Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)branch.Syntax).Else!.Statement;
        Assert.Equal(((Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)nested).Condition.GetLocation().GetLineSpan(), branch.Outcomes[1]);
        Assert.Equal(EvidenceStatus.Complete, Resolve(branch, "yes", "yes"));
    }

    [Fact]
    public void Empty_handler_uses_block_entry_when_it_has_a_sequence_point()
    {
        var compilation = TestCompilation.Create("class C { void M() { try { System.Console.WriteLine(1); } catch { } } }");
        var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken));
        Assert.NotNull(branch.Outcomes[0]);
        Assert.Equal(EvidenceStatus.Exercised, Resolve(branch, "yes"));
        Assert.Equal(EvidenceStatus.NotReached, Resolve(branch, "no"));
    }
    [Fact]
    public void Guards_score_the_rejection_from_condition_and_throw()
    {
        var guard = Guard("class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }");
        var condition = ((Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)guard.Syntax).Condition.GetLocation().GetLineSpan();
        var probe = guard with { Outcomes = [guard.Outcomes[0], condition] };
        Assert.Equal(EvidenceStatus.Exercised, CoverageResolver.Resolve(guard, Read(Xml(probe, "yes", "yes"))));
        Assert.Equal(EvidenceStatus.Untriggered, CoverageResolver.Resolve(guard, Read(Xml(probe, "no", "yes"))));
        Assert.Equal(EvidenceStatus.NotReached, CoverageResolver.Resolve(guard, Read(Xml(probe, "no", "no"))));
    }

    [Fact]
    public void A_guard_without_resolvable_condition_evidence_is_unknown_not_unreached()
    {
        var guard = Guard("class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }");
        Assert.Equal(EvidenceStatus.Unknown, Resolve(guard, "no"));
    }

    [Fact]
    public void A_guard_with_unresolved_throw_evidence_is_unknown_even_when_the_condition_ran()
    {
        var guard = Guard("class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }");
        var condition = ((Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)guard.Syntax).Condition.GetLocation().GetLineSpan();
        var conditionOnly = guard with { Outcomes = [condition] };
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(guard, Read(Xml(conditionOnly, "yes"))));
        var probe = guard with { Outcomes = [guard.Outcomes[0], condition] };
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(guard, Read(Xml(probe, "partial", "yes"))));
    }

    [Fact]
    public void A_partly_executed_range_shared_by_two_branches_is_ambiguous_for_both()
    {
        var compilation = TestCompilation.Create("class C { void M(string s) { if (s?.Length == 0) throw new System.ArgumentException(); } }");
        var branches = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        var expression = Assert.Single(branches, b => b.Measurement == MeasurementKind.Expression);
        Assert.Equal(EvidenceStatus.SharedRange, Resolve(expression, "partial"));
        var lone = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create("class C { string M(string s) => s ?? \"x\"; }"),
            new CollectorScope(), TestContext.Current.CancellationToken), b => b.Measurement == MeasurementKind.Expression);
        Assert.Equal(EvidenceStatus.Partial, Resolve(lone, "partial"));
    }

    [Fact]
    public void A_partly_executed_switch_expression_is_partial_although_its_arms_are_inside_its_range()
    {
        var expression = Decision("class C { string M(int x) => x switch { 1 => \"a\", _ => \"b\" }; }");
        Assert.Equal(EvidenceStatus.Partial, Resolve(expression, "partial"));
    }

    [Fact]
    public void A_partly_executed_expression_in_a_loop_condition_is_partial()
    {
        var expression = Decision("class C { void M(bool? b) { while (b ?? false) { } } }");
        Assert.Equal(EvidenceStatus.Partial, Resolve(expression, "partial"));
    }

    [Fact]
    public void A_partly_executed_filter_that_holds_another_branch_is_shared_range()
    {
        var filter = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create(
            "class C { void M() { try { } catch (System.Exception e) when (e.Message?.Length > 0) { } } }"),
            new CollectorScope(), TestContext.Current.CancellationToken), b => b.Measurement == MeasurementKind.Filter);
        Assert.Equal(EvidenceStatus.SharedRange, Resolve(filter, "partial"));
    }

    [Fact]
    public void An_ambiguous_coverlet_line_does_not_make_an_expression_partial()
    {
        const string source = "class C { int M(bool c, int? a, int b) { if (c) return a ?? b; return 0; } }";
        var branches = BranchInventoryBuilder.Build(TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken);
        var expression = Assert.Single(branches, b => b.Measurement == MeasurementKind.Expression);
        var statement = (Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax)Assert.Single(branches, b => b.Measurement == MeasurementKind.Decision).Syntax;
        // Coverlet line hits: the line ran, and each of its sequence points is marked partly hit.
        var observations = CoverletLine(source, CoverageHit.Partial, statement.Condition, statement.Statement);
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(expression, observations));
    }

    [Fact]
    public void An_ambiguous_coverlet_line_does_not_make_a_filter_partial()
    {
        const string source = "class C { void M() { try { } catch (System.Exception e) when (e.HResult > 0) { } } }";
        var filter = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken),
            b => b.Measurement == MeasurementKind.Filter);
        var clause = (Microsoft.CodeAnalysis.CSharp.Syntax.CatchFilterClauseSyntax)filter.Syntax;
        Assert.Equal(EvidenceStatus.Unknown, CoverageResolver.Resolve(filter, CoverletLine(source, CoverageHit.Partial, clause.FilterExpression)));
    }

    private static CoverageObservations CoverletLine(string source, CoverageHit hit, params Microsoft.CodeAnalysis.SyntaxNode[] points)
    {
        var document = new CoverageSource("fixture.cs", "SHA256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        var ranges = points.Select(point => point.GetLocation().GetLineSpan())
            .Select(span => new CoverageRange(document, span.StartLinePosition, span.EndLinePosition, hit)).ToArray();
        return new CoverageObservations([new CoverageModule("build", "Fixture.dll", ranges) { IsCoverlet = true }]);
    }

    private static InventoryBranch Guard(string source) => Assert.Single(BranchInventoryBuilder.Build(
        TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken), b => b.Measurement == MeasurementKind.Guard);
    private static InventoryBranch Decision(string source) => Assert.Single(BranchInventoryBuilder.Build(
        TestCompilation.Create(source), new CollectorScope(), TestContext.Current.CancellationToken));
    private static EvidenceStatus Resolve(InventoryBranch branch, params string[] states)
        => CoverageResolver.Resolve(branch, Read(Xml(branch, states)));

    private static CoverageObservations Read(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return MicrosoftCoverageXmlReader.Read(stream);
    }

    private static string Xml(InventoryBranch branch, params string[] states)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(branch.Syntax.SyntaxTree.GetText().ToString())));
        var ranges = states.Select((state, i) => branch.Outcomes[i]!.Value).Select((span, i) =>
            $"<range source_id='0' start_line='{span.StartLinePosition.Line + 1}' start_column='{span.StartLinePosition.Character + 1}' end_line='{span.EndLinePosition.Line + 1}' end_column='{span.EndLinePosition.Character + 1}' covered='{states[i]}' />");
        return $"<results><modules><module id='same-build' name='Fixture.dll'><source_files><source_file id='0' path='fixture.cs' checksum_type='SHA256' checksum=\"{checksum}\" /></source_files><functions><function><ranges>{string.Concat(ranges)}</ranges></function></functions></module></modules></results>";
    }
}
