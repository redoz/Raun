using Raun.Coverage.Classification;
using Raun.Coverage.Inventory;
using Raun.Coverage.Projects;
using Raun.Reporting.Coverage;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class ArgumentCheckClassificationTests
{
    [Theory]
    [InlineData("if (x is null) throw new ArgumentNullException(nameof(x));")]
    [InlineData("if (string.IsNullOrWhiteSpace(x) || x.Length > 10) throw new ArgumentException(nameof(x));")]
    [InlineData("if (x is not { Length: > 0 }) { Console.WriteLine(x); throw new ArgumentException(nameof(x)); }")]
    [InlineData("_field = x ?? throw new ArgumentNullException(nameof(x));")]
    [InlineData("_field = (x is null || x.Length == 0) ? throw new ArgumentException(nameof(x)) : x;")]
    [InlineData("_field = x is null ? throw new ArgumentNullException(nameof(x)) : x;")]
    [InlineData("_field = x switch { null => throw new ArgumentNullException(nameof(x)), _ => x };")]
    [InlineData("switch(x) { case null: throw new ArgumentNullException(nameof(x)); default: _field = x; break; }")]
    [InlineData("var error = new DerivedArgument(); if(x is null) throw error;")]
    [InlineData("if(x is null) throw MakeError();")]
    public void Normal_argument_checks_and_their_conditions_are_argument_checks(string body)
    {
        var compilation = TestCompilation.Create("using System; class DerivedArgument : ArgumentException {} class C { string _field = \"\"; static ArgumentException MakeError() => new DerivedArgument(); void M(string x) { " + body + " } }");
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var inventory = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        Assert.NotEmpty(inventory);
        var pipeline = BranchClassificationPipeline.Create([]);
        Assert.All(inventory, branch => Assert.Equal(BranchCategory.ArgumentCheck, pipeline.Classify(branch.Context).Category));
    }

    [Theory]
    [InlineData("ArgumentNullException.ThrowIfNull(x);")]
    [InlineData("ArgumentException.ThrowIfNullOrEmpty(x);")]
    [InlineData("ArgumentException.ThrowIfNullOrWhiteSpace(x);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfNegative(n);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfZero(n);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfEqual(n, 0);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfNotEqual(n, 0);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfGreaterThan(n, 0);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(n, 0);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfLessThan(n, 0);")]
    [InlineData("ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(n, 0);")]
    public void Bcl_argument_helpers_are_listed_without_claiming_the_library_branch_was_exercised(string body)
    {
        var compilation = TestCompilation.Create("using System; class C { void M(string x, int n) { " + body + " } }");
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken));
        Assert.Equal(BranchCategory.ArgumentCheck, BranchClassificationPipeline.Create([]).Classify(branch.Context).Category);
        Assert.Equal(MeasurementKind.Helper, branch.Measurement);
        Assert.Empty(branch.Outcomes);
    }

    [Fact]
    public void Compound_predicate_in_a_lambda_is_not_the_enclosing_argument_guard()
    {
        var compilation = TestCompilation.Create("using System; class C { void M(bool a, bool b) { if(((Func<bool>)(() => a || b))()) throw new ArgumentException(); } }");
        var inventory = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        var pipeline = BranchClassificationPipeline.Create([]);
        Assert.Equal([BranchCategory.ArgumentCheck, BranchCategory.Logic], inventory.Select(branch => pipeline.Classify(branch.Context).Category));
    }

    [Fact]
    public void Compound_checks_keep_throw_measurement_independent_of_classification()
    {
        var compilation = TestCompilation.Create("using System; class C { void M(string x) { if(x is null || x.Length == 0) { Console.WriteLine(x); throw new ArgumentException(); } } }");
        var inventory = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        var guard = Assert.Single(inventory, branch => branch.Descriptor.Form == BranchForm.If);
        Assert.Equal(MeasurementKind.Guard, guard.Measurement);
        Assert.Single(guard.Outcomes);
        Assert.Equal(MeasurementKind.Expression, Assert.Single(inventory, branch => branch.Descriptor.Form == BranchForm.ShortCircuit).Measurement);
    }

    [Fact]
    public void Fallbacks_nested_business_logic_and_catches_keep_their_categories()
    {
        var compilation = TestCompilation.Create("""
            using System;
            class C { string M(string x, bool flag) {
                if(flag) { if(x is null) throw new ArgumentException(); }
                try { return x ?? "default"; } catch(ArgumentException) { return "error"; }
            } }
            """);
        var inventory = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        var pipeline = BranchClassificationPipeline.Create([]);
        Assert.Equal([BranchCategory.Logic, BranchCategory.ArgumentCheck, BranchCategory.NullPropagation, BranchCategory.ErrorHandling],
            inventory.Select(branch => pipeline.Classify(branch.Context).Category));
    }
}
