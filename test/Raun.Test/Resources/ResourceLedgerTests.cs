using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// The scenario-scoped conflict ledger: two steps that nothing orders (no dependency path in either
/// direction) and that both claim one identity, with at least one exclusive verb, are a conflict.
/// Ordered steps never conflict; the same step re-claiming is dedup; nothing waits.
/// </summary>
public class ResourceLedgerTests
{
    private static readonly ResourceIdentity Jane = new(typeof(User), "jane@x");
    private static readonly ResourceIdentity Bob = new(typeof(User), "bob@x");

    private static ScenarioNode Node(int index, int[]? dependsOn = null, Guard[]? guards = null, int[]? mergeSources = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn ?? [],
        Guards = guards ?? [],
        MergeSources = mergeSources ?? [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    // 0 and 1 are siblings (both roots); 2 depends on 1.
    private static ResourceLedger Ledger() => new([Node(0), Node(1), Node(2, dependsOn: [1])]);

    [Fact]
    public void Unordered_exclusive_claims_on_one_identity_conflict()
    {
        var ledger = Ledger();
        ledger.Claim(0, "rename jane", Jane, LifecycleVerb.Edit);

        var ex = Assert.Throws<ResourceConflictException>(
            () => ledger.Claim(1, "delete jane", Jane, LifecycleVerb.Delete));

        Assert.Equal(Jane, ex.Identity);
        Assert.Equal("delete jane", ex.StepDisplayName);
        Assert.Equal(LifecycleVerb.Delete, ex.Verb);
        Assert.Equal("rename jane", ex.OtherStepDisplayName);
        Assert.Equal(LifecycleVerb.Edit, ex.OtherVerb);
        Assert.Contains("rename jane", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delete jane", ex.Message, StringComparison.Ordinal);
        Assert.Contains("User:jane@x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unordered_exclusive_against_shared_conflicts_in_either_order()
    {
        var first = Ledger();
        first.Claim(0, "read", Jane, LifecycleVerb.Read);
        Assert.Throws<ResourceConflictException>(() => first.Claim(1, "edit", Jane, LifecycleVerb.Edit));

        var second = Ledger();
        second.Claim(0, "edit", Jane, LifecycleVerb.Edit);
        Assert.Throws<ResourceConflictException>(() => second.Claim(1, "read", Jane, LifecycleVerb.Read));
    }

    [Fact]
    public void Unordered_shared_claims_coexist()
    {
        var ledger = Ledger();
        ledger.Claim(0, "read", Jane, LifecycleVerb.Read);
        ledger.Claim(1, "load", Jane, LifecycleVerb.Load);
        ledger.Claim(1, "reference", Jane, LifecycleVerb.Reference);
        ledger.Claim(0, "consume", Jane, LifecycleVerb.Consume);
    }

    [Fact]
    public void Different_identities_never_conflict()
    {
        var ledger = Ledger();
        ledger.Claim(0, "edit jane", Jane, LifecycleVerb.Edit);
        ledger.Claim(1, "edit bob", Bob, LifecycleVerb.Edit);
    }

    [Fact]
    public void The_same_step_may_claim_one_identity_repeatedly()
    {
        var ledger = Ledger();
        ledger.Claim(0, "book", Jane, LifecycleVerb.Read);
        ledger.Claim(0, "book", Jane, LifecycleVerb.Edit);
        ledger.Claim(0, "book", Jane, LifecycleVerb.Edit);
    }

    [Fact]
    public void Steps_ordered_by_a_dependency_never_conflict()
    {
        var ledger = Ledger();
        ledger.Claim(1, "create", Jane, LifecycleVerb.Create);
        ledger.Claim(2, "delete", Jane, LifecycleVerb.Delete); // 2 depends on 1
    }

    [Fact]
    public void Ordering_is_transitive()
    {
        // 0 -> 1 -> 2 -> 3: node 3 is ordered after node 0 through two hops.
        var ledger = new ResourceLedger([Node(0), Node(1, [0]), Node(2, [1]), Node(3, [2])]);
        ledger.Claim(0, "create", Jane, LifecycleVerb.Create);
        ledger.Claim(3, "delete", Jane, LifecycleVerb.Delete);
    }

    [Fact]
    public void A_merge_orders_its_consumers_after_the_arm_steps()
    {
        // 0 = condition; 1 and 2 = arms (guarded); 3 = merge over 1,2; 4 depends on the merge.
        var ledger = new ResourceLedger(
        [
            Node(0),
            Node(1, dependsOn: [0], guards: [new Guard(0, 0)]),
            Node(2, dependsOn: [0], guards: [new Guard(0, 1)]),
            Node(3, mergeSources: [1, 2]),
            Node(4, dependsOn: [3]),
        ]);

        ledger.Claim(1, "arm edit", Jane, LifecycleVerb.Edit);
        ledger.Claim(4, "after merge delete", Jane, LifecycleVerb.Delete);
    }

    [Fact]
    public void A_guard_orders_a_step_after_its_condition()
    {
        // 1 is guarded on 0 but declares no DependsOn edge to it.
        var ledger = new ResourceLedger([Node(0), Node(1, guards: [new Guard(0, 0)])]);
        ledger.Claim(0, "condition reads", Jane, LifecycleVerb.Edit);
        ledger.Claim(1, "guarded edit", Jane, LifecycleVerb.Edit);
    }

    [Fact]
    public void Ordering_is_symmetric_for_conflict_purposes()
    {
        // The dependent may claim first; the producer's later claim is still ordered.
        var ledger = Ledger();
        ledger.Claim(2, "dependent", Jane, LifecycleVerb.Edit);
        ledger.Claim(1, "producer", Jane, LifecycleVerb.Edit);
    }
}
