// A union's runtime types (System.Runtime.CompilerServices.IUnion) exist from .NET 11 on.
#if NET11_0_OR_GREATER
using System.Collections.Concurrent;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class UnionBranchingTests
{
    private const string Dsl =
        """
        using System.Collections.Concurrent;
        using System.Threading.Tasks;
        using Raun;

        namespace UnionDemo;

        public sealed record Accepted(string Shipment, int Total);
        public sealed record Rejected(string Reason);
        public readonly record struct Pending(int Minutes);

        public union Outcome(Accepted, Rejected, Pending);

        public static class Trace
        {
            public static readonly ConcurrentQueue<string> Calls = new();
            public static void Add(string call) => Calls.Enqueue(call);

            public static string Describe(Outcome outcome) => outcome switch
            {
                Accepted a => "Accepted:" + a.Shipment,
                Rejected r => "Rejected:" + r.Reason,
                Pending p => "Pending:" + p.Minutes,
            };
        }

        public sealed partial class UnionWhen : When<NoWorld>
        {
            [StepName("submitting {id}")]
            public async Task<Outcome> Submit(string id)
            {
                await Task.Yield();
                Trace.Add("Submit(" + id + ")");
                return id switch
                {
                    "ok" => new Accepted("S1", 10),
                    "big" => new Accepted("S2", 5000),
                    "no" => new Rejected("stock"),
                    "soon" => new Pending(1),
                    _ => new Pending(60),
                };
            }

            [StepName("accepting {shipment}")]
            public async Task<Accepted> Accept(string shipment)
            {
                await Task.Yield();
                Trace.Add("Accept(" + shipment + ")");
                return new Accepted(shipment, 1);
            }

            [StepName("rejecting {reason}")]
            public async Task<Rejected> Reject(string reason)
            {
                await Task.Yield();
                Trace.Add("Reject(" + reason + ")");
                return new Rejected(reason);
            }

            [StepName("waiting {minutes}")]
            public async Task<Pending> Wait(int minutes)
            {
                await Task.Yield();
                Trace.Add("Wait(" + minutes + ")");
                return new Pending(minutes);
            }
        }

        public sealed partial class UnionThen : Then<NoWorld>
        {
            [StepName("note {what}")]
            public Task Note(string what)
            {
                Trace.Add("Note(" + what + ")");
                return Task.CompletedTask;
            }

            [StepName("saw {outcome}")]
            public Task Saw(Outcome outcome)
            {
                Trace.Add("Saw(" + Trace.Describe(outcome) + ")");
                return Task.CompletedTask;
            }
        }

        public abstract class UnionSuite : Scenarios<NoWorld>
        {
            public UnionWhen When => Steps<UnionWhen>();
            public UnionThen Then => Steps<UnionThen>();
        }
        """;

    private static string Scenario(string body) => Dsl +
        $$"""

        public sealed class UnionScenarios : UnionSuite
        {
            [Scenario("union")]
            public async Task Run()
            {
                {{body}}
            }
        }
        """;

    private sealed record UnionRun(IReadOnlyList<StepResult> Results, IReadOnlyList<string> Calls)
    {
        public string Dump() => string.Join("\n", Results.Select(r =>
            $"{r.Node.Index,2} {r.Node.OperationName,-10} {r.Status,-8} {r.DisplayName} {r.SkipReason} {r.Exception?.Message}"))
            + "\ncalls: " + string.Join(", ", Calls);
    }

    private static async Task<UnionRun> RunUnion(string body)
    {
        var result = GeneratorHarness.Run(Scenario(body));
        result.AssertCompiles();
        var definition = Assert.Single(result.Definitions());
        var results = await definition.RunAsync();
        var calls = (ConcurrentQueue<string>)result.Assembly!.GetType("UnionDemo.Trace")!.GetField("Calls")!.GetValue(null)!;
        return new UnionRun(results, [.. calls]);
    }

    private static void AssertRan(UnionRun run, params string[] expectedCalls)
    {
        Assert.True(expectedCalls.SequenceEqual(run.Calls),
            $"expected calls [{string.Join(", ", expectedCalls)}]\n" + run.Dump());
        Assert.True(run.Results.All(r => r.Status is StepStatus.Passed or StepStatus.NotTaken),
            "something failed or was skipped:\n" + run.Dump());
        var passed = run.Results.Count(r => !r.Node.IsSynthetic && !r.Node.IsSetup && !r.Node.IsTeardown && r.Status == StepStatus.Passed);
        Assert.True(passed == expectedCalls.Length,
            $"{passed} business steps passed but {expectedCalls.Length} calls were made:\n" + run.Dump());
    }

    [Theory]
    [InlineData("ok", "Note(accepted S1)")]
    [InlineData("no", "Note(rejected stock)")]
    [InlineData("later", "Note(pending 60)")]
    public async Task An_exhaustive_switch_over_a_union_needs_no_default_and_runs_the_matching_arm(string id, string note)
    {
        var run = await RunUnion($$"""
            switch (await When.Submit("{{id}}"))
            {
                case Accepted accepted:
                    await Then.Note("accepted " + accepted.Shipment);
                    break;
                case Rejected rejected:
                    await Then.Note("rejected " + rejected.Reason);
                    break;
                case Pending pending:
                    await Then.Note("pending " + pending.Minutes);
                    break;
            }
            """);

        AssertRan(run, $"Submit({id})", note);
    }

    [Theory]
    [InlineData("big", "Note(review S2)")]
    [InlineData("ok", "Note(ship S1)")]
    [InlineData("soon", "Note(retry)")]
    [InlineData("later", "Note(give up after 60)")]
    [InlineData("no", "Note(other)")]
    public async Task Property_patterns_and_guards_apply_to_the_case_a_union_holds(string id, string note)
    {
        var run = await RunUnion($$"""
            switch (await When.Submit("{{id}}"))
            {
                case Accepted { Total: > 1000 } big:
                    await Then.Note("review " + big.Shipment);
                    break;
                case Accepted accepted:
                    await Then.Note("ship " + accepted.Shipment);
                    break;
                case Pending { Minutes: < 5 }:
                    await Then.Note("retry");
                    break;
                case Pending pending when pending.Minutes >= 5:
                    await Then.Note("give up after " + pending.Minutes);
                    break;
                default:
                    await Then.Note("other");
                    break;
            }
            """);

        AssertRan(run, $"Submit({id})", note);
    }

    [Theory]
    [InlineData("no", "Note(told stock)")]
    [InlineData("ok", "Note(fine)")]
    public async Task An_is_pattern_condition_tests_the_case_a_union_holds(string id, string note)
    {
        var run = await RunUnion($$"""
            if (await When.Submit("{{id}}") is Rejected rejected)
                await Then.Note("told " + rejected.Reason);
            else
                await Then.Note("fine");
            """);

        AssertRan(run, $"Submit({id})", note);
    }

    [Theory]
    [InlineData("soon", "Note(short)")]
    [InlineData("later", "Note(long or settled)")]
    [InlineData("ok", "Note(long or settled)")]
    public async Task An_is_pattern_on_a_value_type_case_reads_its_members(string id, string note)
    {
        var run = await RunUnion($$"""
            if (await When.Submit("{{id}}") is Pending { Minutes: < 5 })
                await Then.Note("short");
            else
                await Then.Note("long or settled");
            """);

        AssertRan(run, $"Submit({id})", note);
    }

    [Theory]
    [InlineData("ok", "Accept(S1x)", "Saw(Accepted:S1x)")]
    [InlineData("no", "Reject(stock!)", "Saw(Rejected:stock!)")]
    [InlineData("later", "Wait(61)", "Saw(Pending:61)")]
    public async Task A_union_local_takes_a_case_typed_step_result_in_each_arm(string id, string step, string saw)
    {
        var run = await RunUnion($$"""
            Outcome o = await When.Submit("big");
            switch (await When.Submit("{{id}}"))
            {
                case Accepted accepted:
                    o = await When.Accept(accepted.Shipment + "x");
                    break;
                case Rejected rejected:
                    o = await When.Reject(rejected.Reason + "!");
                    break;
                case Pending pending:
                    o = await When.Wait(pending.Minutes + 1);
                    break;
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(big)", $"Submit({id})", step, saw);
    }

    [Fact]
    public async Task A_local_declared_as_the_union_holds_a_case_typed_step_result()
    {
        var run = await RunUnion("""
            Outcome o = await When.Accept("A1");
            await Then.Saw(o);
            Outcome p = await When.Wait(3);
            await Then.Saw(p);
            """);

        AssertRan(run, "Accept(A1)", "Saw(Accepted:A1)", "Wait(3)", "Saw(Pending:3)");
    }

    [Theory]
    [InlineData("no", "Saw(Rejected:stock)")]
    [InlineData("ok", "Saw(Accepted:A1)")]
    public async Task A_union_local_keeps_its_case_typed_value_through_an_arm_that_leaves_it_alone(string id, string saw)
    {
        var run = await RunUnion($$"""
            Outcome o = await When.Accept("A1");
            if (await When.Submit("{{id}}") is Rejected rejected)
                o = await When.Reject(rejected.Reason);
            await Then.Saw(o);
            """);

        string[] expected = id == "no"
            ? ["Accept(A1)", "Submit(no)", "Reject(stock)", saw]
            : ["Accept(A1)", "Submit(ok)", saw];
        AssertRan(run, expected);
    }

    [Theory]
    [InlineData("no", "ok", "Saw(Rejected:stock)")]
    [InlineData("ok", "ok", "Saw(Accepted:inner)")]
    [InlineData("ok", "later", "Saw(Pending:7)")]
    [InlineData("ok", "no", "Saw(Accepted:S1)")]
    public async Task A_union_local_merges_case_typed_results_from_nested_branches(string outer, string inner, string saw)
    {
        var run = await RunUnion($$"""
            var o = await When.Submit("ok");
            if (await When.Submit("{{outer}}") is Accepted)
            {
                switch (await When.Submit("{{inner}}"))
                {
                    case Accepted:
                        o = await When.Accept("inner");
                        break;
                    case Pending:
                        o = await When.Wait(7);
                        break;
                }
            }
            else
            {
                o = await When.Reject("stock");
            }
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Submit({outer})" };
        if (outer == "ok")
        {
            expected.Add($"Submit({inner})");
            if (inner == "ok") expected.Add("Accept(inner)");
            if (inner == "later") expected.Add("Wait(7)");
        }
        else
        {
            expected.Add("Reject(stock)");
        }

        expected.Add(saw);
        AssertRan(run, [.. expected]);
    }

    [Fact]
    public async Task Union_locals_of_a_tuple_group_hold_case_typed_step_results()
    {
        var run = await RunUnion("""
            (Outcome a, Outcome b) = await (When.Accept("A1"), When.Wait(2));
            await Then.Saw(a);
            await Then.Saw(b);
            """);

        Assert.Equal(["Accept(A1)", "Wait(2)"], run.Calls.Take(2).Order(StringComparer.Ordinal));
        Assert.Equal(["Saw(Accepted:A1)", "Saw(Pending:2)"], run.Calls.Skip(2));
        Assert.All(run.Results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Theory]
    [InlineData(true, "Note(dog)")]
    [InlineData(false, "Note(pet)")]
    public async Task A_merged_union_local_converts_a_derived_case_before_the_case_it_derives_from(bool dog, string note)
    {
        var source = Dsl.Replace("public static class Trace", """
            public record Pet(string Name);
            public sealed record Dog(string Name) : Pet(Name);
            public union Animal(Pet, Dog);

            public sealed partial class PetWhen : When<NoWorld>
            {
                [StepName("a pet")]
                public async Task<Pet> APet() { await Task.Yield(); Trace.Add("APet"); return new Pet("p"); }

                [StepName("a dog")]
                public async Task<Dog> ADog() { await Task.Yield(); Trace.Add("ADog"); return new Dog("d"); }

                [StepName("is it a dog {yes}")]
                public async Task<bool> IsDog(bool yes) { await Task.Yield(); Trace.Add("IsDog"); return yes; }

                [StepName("naming {animal}")]
                public Task Name(Animal animal)
                {
                    Trace.Add("Note(" + (animal is Dog ? "dog" : "pet") + ")");
                    return Task.CompletedTask;
                }
            }

            public static class Trace
            """, StringComparison.Ordinal) +
            $$"""

            public sealed class PetScenarios : UnionSuite
            {
                public PetWhen Pets => Steps<PetWhen>();

                [Scenario("pets")]
                public async Task Run()
                {
                    Animal animal;
                    if (await Pets.IsDog({{(dog ? "true" : "false")}}) is false)
                        animal = await Pets.APet();
                    else
                        animal = await Pets.ADog();
                    await Pets.Name(animal);
                }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        var results = await Assert.Single(result.Definitions()).RunAsync();
        var calls = (ConcurrentQueue<string>)result.Assembly!.GetType("UnionDemo.Trace")!.GetField("Calls")!.GetValue(null)!;
        AssertRan(new UnionRun(results, [.. calls]), "IsDog", dog ? "ADog" : "APet", note);
    }

    [Fact]
    public async Task A_case_typed_step_result_is_passed_where_a_step_takes_the_union()
    {
        var run = await RunUnion("""
            var accepted = await When.Accept("A1");
            await Then.Saw(accepted);
            var pending = await When.Wait(3);
            await Then.Saw(pending);
            """);

        AssertRan(run, "Accept(A1)", "Saw(Accepted:A1)", "Wait(3)", "Saw(Pending:3)");
    }

    [Theory]
    [InlineData("ok", "Note(known)")]
    [InlineData("later", "Note(pending)")]
    public async Task A_switch_that_leaves_a_case_out_runs_nothing_for_it_but_its_default(string id, string note)
    {
        var run = await RunUnion($$"""
            switch (await When.Submit("{{id}}"))
            {
                case Pending:
                    await Then.Note("pending");
                    break;
                default:
                    await Then.Note("known");
                    break;
            }
            """);

        AssertRan(run, $"Submit({id})", note);
    }

    [Theory]
    [InlineData(1, "Note(circle 1)")]
    [InlineData(2, "Note(square 2)")]
    public async Task An_exhaustive_switch_over_a_closed_hierarchy_needs_no_default(int sides, string note)
    {
        var source = Dsl.Replace("public static class Trace", """
            public closed record Shape;
            public sealed record Circle(int Radius) : Shape;
            public sealed record Square(int Side) : Shape;

            public sealed partial class ShapeWhen : When<NoWorld>
            {
                [StepName("drawing {n}")]
                public async Task<Shape> Draw(int n)
                {
                    await Task.Yield();
                    Trace.Add("Draw(" + n + ")");
                    return n == 1 ? new Circle(n) : new Square(n);
                }
            }

            public static class Trace
            """, StringComparison.Ordinal) +
            $$"""

            public sealed class ShapeScenarios : UnionSuite
            {
                public ShapeWhen Shapes => Steps<ShapeWhen>();

                [Scenario("closed")]
                public async Task Run()
                {
                    switch (await Shapes.Draw({{sides}}))
                    {
                        case Circle circle:
                            await Then.Note("circle " + circle.Radius);
                            break;
                        case Square square:
                            await Then.Note("square " + square.Side);
                            break;
                    }
                }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        var results = await Assert.Single(result.Definitions()).RunAsync();
        var calls = (ConcurrentQueue<string>)result.Assembly!.GetType("UnionDemo.Trace")!.GetField("Calls")!.GetValue(null)!;
        AssertRan(new UnionRun(results, [.. calls]), $"Draw({sides})", note);
    }
}
#endif
