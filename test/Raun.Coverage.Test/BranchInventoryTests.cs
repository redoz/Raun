using Raun.Coverage.Inventory;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class BranchInventoryTests
{
    [Fact]
    public void Nested_types_keep_their_semantic_namespace()
    {
        var compilation = TestCompilation.Create("namespace Business { class Outer { class Inner { int M(bool b) => b ? 1 : 2; } } }");
        var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new Raun.Coverage.Projects.CollectorScope(), TestContext.Current.CancellationToken));
        Assert.Equal("Business", branch.Descriptor.Member.Namespace);
        Assert.Equal("Business.Outer.Inner", branch.Descriptor.Member.Type);
        var global = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create("class C { int M(bool b) => b ? 1 : 2; }"),
            new Raun.Coverage.Projects.CollectorScope(), TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, global.Descriptor.Member.Namespace);
    }
    [Fact]
    public void Enumerates_source_decisions_and_keeps_overloads_distinct()
    {
        var compilation = TestCompilation.Create("""
            class C {
                int M(int x) { if (x > 0) return 1; else return 2; }
                int M(string x) => x?.Length ?? 0;
                int S(int x) { switch (x) { case 1: return 1; default: return 2; } }
            }
            """);
        var inventory = BranchInventoryBuilder.Build(compilation, new Raun.Coverage.Projects.CollectorScope(), TestContext.Current.CancellationToken);
        Assert.Equal(4, inventory.Count);
        Assert.Equal(3, inventory.Select(b => b.Descriptor.Member.Signature).Distinct().Count());
        var decision = Assert.Single(inventory, b => b.Descriptor.Form == BranchForm.If);
        Assert.Equal(2, decision.Outcomes.Count);
        Assert.All(decision.Outcomes, location => Assert.NotNull(location));
    }

    [Fact]
    public void Guards_helpers_filters_and_fallthrough_have_independent_structural_roles()
    {
        var compilation = TestCompilation.Create("""
            using System;
            class C { int M(string x, bool flag) {
                ArgumentNullException.ThrowIfNull(x);
                if (x.Length == 0) throw new ArgumentException();
                if (flag) Console.WriteLine(x);
                try { return 1; } catch (Exception e) when (e.Message != null) { return 2; }
            } }
            """);
        var inventory = BranchInventoryBuilder.Build(compilation, new Raun.Coverage.Projects.CollectorScope(), TestContext.Current.CancellationToken);
        Assert.Equal(5, inventory.Count);
        Assert.Single(inventory, b => b.Measurement == MeasurementKind.Helper);
        Assert.Single(inventory, b => b.Measurement == MeasurementKind.Guard);
        Assert.Single(inventory, b => b.Measurement == MeasurementKind.Handler);
        Assert.Single(inventory, b => b.Measurement == MeasurementKind.Filter);
        var fallthrough = Assert.Single(inventory, b => b.Measurement == MeasurementKind.Decision);
        Assert.Null(fallthrough.Outcomes[1]);
    }

    [Fact]
    public void Excluded_property_and_local_function_bodies_leave_inventory()
    {
        var compilation = TestCompilation.Create("""
            using System.Diagnostics.CodeAnalysis;
            class C {
                [ExcludeFromCodeCoverage] int P { get { return true ? 1 : 2; } }
                int M() {
                    [ExcludeFromCodeCoverage] int Local() { return true ? 1 : 2; }
                    return Local();
                }
            }
            """);
        Assert.Empty(BranchInventoryBuilder.Build(compilation, new Raun.Coverage.Projects.CollectorScope(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Lambda_and_local_function_branches_belong_to_declared_member()
    {
        var compilation = TestCompilation.Create("""
            using System;
            class C { int M(bool flag) {
                Func<int> f = () => flag ? 1 : 2;
                int Local() { if (flag) return 1; else return 2; }
                return f() + Local();
            } }
            """);
        var inventory = BranchInventoryBuilder.Build(compilation, new Raun.Coverage.Projects.CollectorScope(), TestContext.Current.CancellationToken);
        Assert.Equal(2, inventory.Count);
        Assert.All(inventory, b => Assert.Equal("C.M(bool)", b.Descriptor.Member.Signature));
        Assert.Equal(2, inventory.Select(b => b.Descriptor.Id).Distinct().Count());
    }
}
