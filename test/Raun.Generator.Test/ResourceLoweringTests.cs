using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator lowers resource role attributes ([Created]/[Edited]/[Read] on a step's return value
/// or parameters) into <c>ctx.Resources.*</c> verb calls, so a step's recorded effect stream reflects
/// its declared roles. A role-free scenario must emit no resource calls at all.
/// </summary>
public class ResourceLoweringTests
{
    private static async Task<IReadOnlyList<StepResult>> RunResourceScenario()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.ResourceScenario);
        result.AssertCompiles();
        return await result.Definitions().Single().RunAsync();
    }

    [Fact]
    public async Task Return_creates_records_a_create_effect()
    {
        var results = await RunResourceScenario();

        // Given.UserExists → [return: Created]
        var effect = Assert.Single(results[1].Effects);
        Assert.Equal(LifecycleVerb.Create, effect.Verb);
        Assert.Equal("User:jane@acme.com", effect.Identity.ToString());
    }

    [Fact]
    public async Task Param_and_return_edits_dedup_to_one_edit_effect()
    {
        var results = await RunResourceScenario();

        // When.Suspend([Edited] User) → [return: Edited]; both resolve to the same identity ⇒ one effect.
        var effect = Assert.Single(results[2].Effects);
        Assert.Equal(LifecycleVerb.Edit, effect.Verb);
        Assert.Equal("User:jane@acme.com", effect.Identity.ToString());
    }

    [Fact]
    public async Task Param_reads_records_a_read_effect()
    {
        var results = await RunResourceScenario();

        // Then.CannotSignIn([Read] User)
        var effect = Assert.Single(results[3].Effects);
        Assert.Equal(LifecycleVerb.Read, effect.Verb);
        Assert.Equal("User:jane@acme.com", effect.Identity.ToString());
    }

    [Fact]
    public void Role_free_scenario_emits_no_resource_calls()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();
        Assert.DoesNotContain(".Resources.", result.GeneratedSource);
    }

    [Fact]
    public async Task Multi_param_roles_emit_in_param_then_return_order()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.BookingScenario);
        result.AssertCompiles();
        var results = await result.Definitions().Single().RunAsync();

        // Step 0: Given.UserExists → single [return: Created] on User:jane@acme.com.
        var userCreate = Assert.Single(results[1].Effects);
        Assert.Equal(LifecycleVerb.Create, userCreate.Verb);
        Assert.Equal("User:jane@acme.com", userCreate.Identity.ToString());

        // Step 1: Given.SlotExists → single [return: Created] on Slot:1.
        var slotCreate = Assert.Single(results[2].Effects);
        Assert.Equal(LifecycleVerb.Create, slotCreate.Verb);
        Assert.Equal("Slot:1", slotCreate.Identity.ToString());

        // Step 2: When.Book([Read] User, [Edited] Slot) [return: Created] → effects must appear in
        // exactly this order: each role-bearing parameter in declaration order, THEN the return role.
        var book = results[3].Effects;
        Assert.Equal(3, book.Count);

        Assert.Equal(LifecycleVerb.Read, book[0].Verb);
        Assert.Equal("User:jane@acme.com", book[0].Identity.ToString());

        Assert.Equal(LifecycleVerb.Edit, book[1].Verb);
        Assert.Equal("Slot:1", book[1].Identity.ToString());

        Assert.Equal(LifecycleVerb.Create, book[2].Verb);
        Assert.Equal("Appointment:jane@acme.com@1", book[2].Identity.ToString());
    }

    [Fact]
    public void Reference_and_consume_subjects_emit_edge_calls()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
        result.AssertCompiles();

        // [return: Created(References = [nameof(user)], Consumes = [nameof(slot)])] emits each lineage call
        // with the created Appointment (__r) riding along as the subject.
        // Note: the target expression is __inputs.Get<T>(n) which contains parens, so .* is used instead of [^)]*.
        Assert.Matches(@"Resources\.Reference\(.*,\s*__r\)", result.GeneratedSource);
        Assert.Matches(@"Resources\.Consume\(.*,\s*__r\)", result.GeneratedSource);
    }

    [Fact]
    public async Task Reference_and_consume_params_lower_to_shared_lineage_effects()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
        result.AssertCompiles();
        var results = await result.Definitions().Single().RunAsync();

        // Step 2: When.BookWithLineage(User user, Slot slot) [return: Created(References = [nameof(user)],
        // Consumes = [nameof(slot)])] — the producer's lineage emits Reference(user) then Consume(slot)
        // before the return's Create, so effects read Reference, Consume, Create.
        var book = results[3].Effects;
        Assert.Equal(3, book.Count);

        Assert.Equal(LifecycleVerb.Reference, book[0].Verb);
        Assert.Equal(LockMode.Shared, book[0].Mode);
        Assert.Equal("User:jane@acme.com", book[0].Identity.ToString());

        Assert.Equal(LifecycleVerb.Consume, book[1].Verb);
        Assert.Equal(LockMode.Shared, book[1].Mode);
        Assert.Equal("Slot:1", book[1].Identity.ToString());

        Assert.Equal(LifecycleVerb.Create, book[2].Verb);
        Assert.Equal("Appointment:jane@acme.com@1", book[2].Identity.ToString());
    }

    [Fact]
    public async Task BookWithLineage_records_relations_from_the_created_appointment()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
        result.AssertCompiles();
        var results = await result.Definitions().Single().RunAsync();

        // Step 2: BookWithLineage(User user, Slot slot) [return: Created(References = [nameof(user)], Consumes = [nameof(slot)])] Appointment
        var relations = results[3].Lineage;
        Assert.Equal(2, relations.Count);

        var reference = relations.Single(e => e.Kind == LifecycleVerb.Reference);
        Assert.Equal("Appointment:jane@acme.com@1", reference.Subject.ToString());
        Assert.Equal("User:jane@acme.com", reference.Target.ToString());

        var consume = relations.Single(e => e.Kind == LifecycleVerb.Consume);
        Assert.Equal("Appointment:jane@acme.com@1", consume.Subject.ToString());
        Assert.Equal("Slot:1", consume.Target.ToString());
    }

    [Fact]
    public void Param_role_claims_are_emitted_before_the_call_and_return_claims_after()
    {
        // Declare what you touch, then touch it: a parameter claim that conflicts with an unordered
        // sibling's must be refused BEFORE the step performs its real side effect. The return claim can
        // only follow the call, because __r does not exist until then.
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.ResourceScenario);
        result.AssertCompiles();
        var source = result.GeneratedSource;

        var paramClaim = source.IndexOf("__ctx.Resources.Edit(__inputs.Get<global::ResourceDemo.User>(1))", StringComparison.Ordinal);
        var call = source.IndexOf("Steps<global::ResourceDemo.ResourceWhen>().Suspend(", StringComparison.Ordinal);
        var returnClaim = source.IndexOf("__ctx.Resources.Edit(__r)", StringComparison.Ordinal);

        Assert.True(paramClaim >= 0 && call >= 0 && returnClaim >= 0, "expected all three statements in the generated source");
        Assert.True(paramClaim < call, "the parameter claim must precede the DSL call");
        Assert.True(call < returnClaim, "the return claim must follow the DSL call");
    }

    [Fact]
    public void Lineage_claims_whose_subject_is_the_return_are_emitted_after_the_call()
    {
        // Reference(user, __r) names the return as its subject, so it cannot move before the call even
        // though its target is a parameter.
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
        result.AssertCompiles();
        var source = result.GeneratedSource;

        var call = source.IndexOf("Steps<global::ResourceDemo.ResourceWhen>().BookWithLineage(", StringComparison.Ordinal);
        var reference = source.IndexOf("__ctx.Resources.Reference(", StringComparison.Ordinal);

        Assert.True(call >= 0 && reference >= 0, "expected the call and the lineage claim in the generated source");
        Assert.True(call < reference, "a lineage claim with the return as subject must follow the call");
    }
}
