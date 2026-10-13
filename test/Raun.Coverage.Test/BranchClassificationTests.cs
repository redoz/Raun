using Raun.Coverage.Classification;
using Raun.Coverage.Inventory;
using Raun.Coverage.Projects;
using Raun.Reporting.Coverage;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class BranchClassificationTests
{
    [Fact]
    public void Middleware_runs_in_registration_order_and_can_delegate_or_short_circuit()
    {
        var branch = Branch();
        var calls = new List<string>();
        var pipeline = BranchClassificationPipeline.Create([
            () => new Rule((_, next) => { calls.Add("outer"); var result = next(); calls.Add("after"); return result; }),
            () => new Rule((_, _) => { calls.Add("inner"); return BranchCategory.Logging; }),
        ]);
        Assert.Equal(BranchCategory.Logging, pipeline.Classify(branch.Context).Category);
        Assert.Equal(["outer", "inner", "after"], calls);
        Assert.Equal(MeasurementKind.Decision, branch.Measurement);
        Assert.Equal(2, branch.Outcomes.Count);
    }

    [Fact]
    public void Default_classifier_uses_exception_semantics_and_helpers()
    {
        var compilation = TestCompilation.Create("""
            using System;
            class MyArgument : ArgumentException {}
            class C { int M(string x) {
                ArgumentNullException.ThrowIfNull(x);
                if (x.Length == 0) throw new MyArgument();
                if (x.Length == 1) throw new InvalidOperationException();
                return x?.Length ?? 0;
            } }
            """);
        var branches = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
        var pipeline = BranchClassificationPipeline.Create([]);
        Assert.Equal([BranchCategory.ArgumentCheck, BranchCategory.ArgumentCheck, BranchCategory.ErrorHandling,
            BranchCategory.NullPropagation, BranchCategory.NullPropagation],
            branches.Select(b => pipeline.Classify(b.Context).Category));
    }

    [Fact]
    public void Failed_middleware_does_not_fall_back_or_change_inventory()
    {
        var branch = Branch();
        var pipeline = BranchClassificationPipeline.Create([
            () => new Rule((_, next) => { next(); return next(); }),
        ]);
        var result = pipeline.Classify(branch.Context);
        Assert.Null(result.Category);
        Assert.Single(result.Diagnostics);
        Assert.Equal(2, branch.Outcomes.Count);
        Assert.Equal(BranchCategory.Logic, BranchClassificationPipeline.Create([]).Classify(branch.Context).Category);
    }

    [Fact]
    public void Swapping_middleware_changes_categories_only()
    {
        var branches = BranchInventoryBuilder.Build(TestCompilation.Create(
            "class C { int M(bool b) { if (b) return 1; else return 2; } }"), new CollectorScope(), TestContext.Current.CancellationToken);
        var branch = Assert.Single(branches);
        var identity = branch.Descriptor;
        var original = BranchClassificationPipeline.Create([]).Classify(branch.Context);
        var changed = BranchClassificationPipeline.Create([() => new Rule((_, _) => BranchCategory.ArgumentCheck)]).Classify(branch.Context);
        Assert.Equal(BranchCategory.Logic, original.Category);
        Assert.Equal(BranchCategory.ArgumentCheck, changed.Category);
        Assert.Same(identity, branch.Descriptor);
        Assert.Equal(MeasurementKind.Decision, branch.Measurement);
    }

    [Fact]
    public void Nested_throw_does_not_reclassify_enclosing_logic()
    {
        var branches = BranchInventoryBuilder.Build(TestCompilation.Create(
            "using System; class C { void M(bool a, bool b) { if(a) { if(b) throw new ArgumentException(); } } }"),
            new CollectorScope(), TestContext.Current.CancellationToken);
        var pipeline = BranchClassificationPipeline.Create([]);
        Assert.Equal([BranchCategory.Logic, BranchCategory.ArgumentCheck], branches.Select(b => pipeline.Classify(b.Context).Category));
    }

    private static InventoryBranch Branch() => Assert.Single(BranchInventoryBuilder.Build(
        TestCompilation.Create("class C { int M(bool b) { if (b) return 1; else return 2; } }"),
        new CollectorScope(), TestContext.Current.CancellationToken));

    private sealed class Rule(Func<BranchClassificationContext, BranchClassificationNext, BranchCategory> classify) : IBranchClassificationMiddleware
    {
        public BranchCategory Classify(BranchClassificationContext context, BranchClassificationNext next) => classify(context, next);
    }
}
