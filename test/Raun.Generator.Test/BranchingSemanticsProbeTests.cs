using System.Collections.Concurrent;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Correctness probes for branching: the oracle is plain C#. Every step records its call (with its
/// argument values) into a static trace; the expected trace is computed by hand from C# semantics and
/// compared with what Raun ran. Besides the trace, every probe asserts that nothing Failed or was
/// Skipped (unless the probe is about failure), and that the number of business steps reported Passed
/// equals the number of calls made — so a step can never be reported Passed without having run.
/// </summary>
public class BranchingSemanticsProbeTests
{
    private const string ProbeDsl =
        """
        using System.Collections.Concurrent;
        using System.Threading.Tasks;
        using Raun;

        namespace ProbeDemo;

        public abstract record Outcome;
        public sealed record Accepted(string Shipment, int Total, bool Express) : Outcome;
        public sealed record Rejected(string Reason) : Outcome;
        public sealed record Pending : Outcome;

        public static class Trace
        {
            public static readonly ConcurrentQueue<string> Calls = new();
            public static void Add(string call) => Calls.Enqueue(call);

            public static string Describe(Outcome outcome) => outcome switch
            {
                Accepted a => "Accepted:" + a.Shipment,
                Rejected r => "Rejected:" + r.Reason,
                _ => "Pending",
            };
        }

        public static class Probe
        {
            public static bool Boom(Outcome outcome) => throw new System.InvalidOperationException("boom");
            public static bool IsBig(Accepted a) => a.Total > 150;
        }

        public sealed partial class ProbeWhen : When<NoWorld>
        {
            [StepName("submitting {id}")]
            public async Task<Outcome> Submit(string id)
            {
                await Task.Yield();
                Trace.Add("Submit(" + id + ")");
                return id switch
                {
                    "ok" => new Accepted("S1", 10, false),
                    "big" => new Accepted("S2", 5000, false),
                    "fast" => new Accepted("S3", 10, true),
                    "no" => new Rejected("stock"),
                    _ => new Pending(),
                };
            }

            [StepName("flag {value}")]
            public async Task<bool> Flag(bool value)
            {
                await Task.Yield();
                Trace.Add("Flag(" + value + ")");
                return value;
            }

            [StepName("number {n}")]
            public async Task<int> Number(int n)
            {
                await Task.Yield();
                Trace.Add("Number(" + n + ")");
                return n;
            }

            [StepName("text {s}")]
            public async Task<string> Text(string s)
            {
                await Task.Yield();
                Trace.Add("Text(" + s + ")");
                return s;
            }

            [StepName("accepting {shipment}")]
            public async Task<Accepted> Accept(string shipment)
            {
                await Task.Yield();
                Trace.Add("Accept(" + shipment + ")");
                return new Accepted(shipment, shipment.Length * 100, false);
            }

            [StepName("rejecting {reason}")]
            public async Task<Rejected> Reject(string reason)
            {
                await Task.Yield();
                Trace.Add("Reject(" + reason + ")");
                return new Rejected(reason);
            }

            [StepName("failing {id}")]
            public async Task<Outcome> Fail(string id)
            {
                await Task.Yield();
                Trace.Add("Fail(" + id + ")");
                throw new System.InvalidOperationException("fail " + id);
            }

            [StepName("slow {id}")]
            public async Task Slow(string id)
            {
                await Task.Delay(150);
                Trace.Add("Slow(" + id + ")");
            }
        }

        public sealed partial class ProbeThen : Then<NoWorld>
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

        public abstract class ProbeSuite : Scenarios<NoWorld>
        {
            public ProbeWhen When => Steps<ProbeWhen>();
            public ProbeThen Then => Steps<ProbeThen>();
        }
        """;

    private static string Scenario(string body) => ProbeDsl +
        $$"""

        public sealed class ProbeScenarios : ProbeSuite
        {
            [Scenario("probe")]
            public async Task Run()
            {
                {{body}}
            }
        }
        """;

    private sealed record ProbeRun(IReadOnlyList<StepResult> Results, IReadOnlyList<string> Calls, string Generated)
    {
        public IEnumerable<StepResult> Business => Results.Where(r => !r.Node.IsSynthetic && !r.Node.IsSetup && !r.Node.IsTeardown);

        public StepResult Step(string operation, string? displayContains = null)
            => Results.Single(r => r.Node.OperationName == operation
                && (displayContains is null || r.DisplayName.Contains(displayContains, StringComparison.Ordinal)));

        public string Dump() => string.Join("\n", Results.Select(r =>
            $"{r.Node.Index,2} {r.Node.OperationName,-10} {r.Status,-8} {r.DisplayName} {r.SkipReason} {r.Exception?.Message}"))
            + "\ncalls: " + string.Join(", ", Calls);
    }

    private static async Task<ProbeRun> RunProbe(string body)
    {
        var result = GeneratorHarness.Run(Scenario(body));
        result.AssertCompiles();
        var definition = Assert.Single(result.Definitions());
        var results = await definition.RunAsync();
        var calls = (ConcurrentQueue<string>)result.Assembly!.GetType("ProbeDemo.Trace")!.GetField("Calls")!.GetValue(null)!;
        return new ProbeRun(results, [.. calls], result.GeneratedSource);
    }

    /// <summary>The oracle: exactly these calls in this order, nothing failed or skipped, and exactly as
    /// many business steps passed as calls were made (every other business step is NotTaken).</summary>
    private static void AssertRan(ProbeRun run, params string[] expectedCalls)
    {
        Assert.True(expectedCalls.SequenceEqual(run.Calls),
            $"expected calls [{string.Join(", ", expectedCalls)}]\n" + run.Dump());
        Assert.True(run.Results.All(r => r.Status is StepStatus.Passed or StepStatus.NotTaken),
            "something failed or was skipped:\n" + run.Dump());
        var passed = run.Business.Count(r => r.Status == StepStatus.Passed);
        Assert.True(passed == expectedCalls.Length,
            $"{passed} business steps passed but {expectedCalls.Length} calls were made:\n" + run.Dump());
    }

    /// <summary>Same as <see cref="AssertRan"/> but the calls may come in any order (parallel groups).</summary>
    private static void AssertRanUnordered(ProbeRun run, params string[] expectedCalls)
    {
        Assert.True(expectedCalls.OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(run.Calls.OrderBy(c => c, StringComparer.Ordinal)),
            $"expected calls (any order) [{string.Join(", ", expectedCalls)}]\n" + run.Dump());
        Assert.True(run.Results.All(r => r.Status is StepStatus.Passed or StepStatus.NotTaken),
            "something failed or was skipped:\n" + run.Dump());
        var passed = run.Business.Count(r => r.Status == StepStatus.Passed);
        Assert.True(passed == expectedCalls.Length,
            $"{passed} business steps passed but {expectedCalls.Length} calls were made:\n" + run.Dump());
    }

    private static void AssertNotTaken(ProbeRun run, params string[] operations)
    {
        foreach (var op in operations)
        {
            Assert.True(run.Results.Where(r => r.Node.OperationName == op).All(r => r.Status == StepStatus.NotTaken),
                $"expected every '{op}' not taken:\n" + run.Dump());
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Nesting: switch in if, if-is in switch, switch in switch; locals reassigned at several depths.
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "no", "Rejected:stock!")]
    [InlineData(true, "ok", "Accepted:S1x")]
    [InlineData(true, "later", "Accepted:A1")]
    [InlineData(false, "no", "Accepted:S1")]
    public async Task P01_switch_inside_an_if_arm_reassigning_a_local_at_both_depths(bool flag, string key, string saw)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            if (await When.Flag({{(flag ? "true" : "false")}}))
            {
                o = await When.Accept("A1");
                switch (await When.Submit("{{key}}"))
                {
                    case Rejected r:
                        o = await When.Reject(r.Reason + "!");
                        break;
                    case Accepted a:
                        o = await When.Accept(a.Shipment + "x");
                        break;
                }
            }
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Flag({flag})" };
        if (flag)
        {
            expected.Add("Accept(A1)");
            expected.Add($"Submit({key})");
            if (key == "no") expected.Add("Reject(stock!)");
            if (key == "ok") expected.Add("Accept(S1x)");
        }

        expected.Add($"Saw({saw})");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData("fast", "no", "Rejected:stock")]
    [InlineData("fast", "ok", "Accepted:S1")]
    [InlineData("no", "ok", "Accepted:D")]
    public async Task P02_if_is_inside_a_switch_section(string outer, string inner, string saw)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            switch (await When.Submit("{{outer}}"))
            {
                case Accepted a:
                    if (await When.Submit("{{inner}}") is Rejected r)
                        o = await When.Reject(r.Reason);
                    else
                        await Then.Note("kept " + a.Shipment);
                    break;
                default:
                    o = await When.Accept("D");
                    break;
            }
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Submit({outer})" };
        if (outer == "fast")
        {
            expected.Add($"Submit({inner})");
            expected.Add(inner == "no" ? "Reject(stock)" : "Note(kept S3)");
        }
        else
        {
            expected.Add("Accept(D)");
        }

        expected.Add($"Saw({saw})");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData("ok", "no", "Rejected:stock")]
    [InlineData("ok", "later", "Accepted:S1p")]
    [InlineData("ok", "ok", "Accepted:S1")]
    [InlineData("no", "ok", "Rejected:stock2")]
    [InlineData("later", "ok", "Accepted:S1")]
    public async Task P03_switch_inside_a_switch_both_default_less(string k1, string k2, string saw)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            switch (await When.Submit("{{k1}}"))
            {
                case Accepted a:
                    switch (await When.Submit("{{k2}}"))
                    {
                        case Rejected r:
                            o = await When.Reject(r.Reason);
                            break;
                        case Pending:
                            o = await When.Accept(a.Shipment + "p");
                            break;
                    }
                    break;
                case Rejected r2:
                    o = await When.Reject(r2.Reason + "2");
                    break;
            }
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Submit({k1})" };
        if (k1 == "ok")
        {
            expected.Add($"Submit({k2})");
            if (k2 == "no") expected.Add("Reject(stock)");
            if (k2 == "later") expected.Add("Accept(S1p)");
        }
        else if (k1 == "no")
        {
            expected.Add("Reject(stock2)");
        }

        expected.Add($"Saw({saw})");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData(true, true, "Rejected:stock!")]
    [InlineData(true, false, "Accepted:S1")]
    [InlineData(false, true, "Accepted:S1")]
    public async Task P04_if_in_switch_in_if_reassigning_only_at_the_deepest_level(bool f1, bool f2, string saw)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            if (await When.Flag({{(f1 ? "true" : "false")}}))
                switch (await When.Submit("no"))
                {
                    case Rejected r:
                        if (await When.Flag({{(f2 ? "true" : "false")}}))
                            o = await When.Reject(r.Reason + "!");
                        break;
                }
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Flag({f1})" };
        if (f1)
        {
            expected.Add("Submit(no)");
            expected.Add($"Flag({f2})");
            if (f2) expected.Add("Reject(stock!)");
        }

        expected.Add($"Saw({saw})");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData(true, "in")]
    [InlineData(false, "pre")]
    public async Task P05_local_assigned_in_outer_arm_then_in_nested_arm_read_at_both_levels(bool inner, string shipment)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            if (await When.Flag(true))
            {
                o = await When.Accept("pre");
                if (await When.Flag({{(inner ? "true" : "false")}}))
                    o = await When.Accept("in");
                await Then.Saw(o);
            }
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", "Flag(True)", "Accept(pre)", $"Flag({inner})" };
        if (inner) expected.Add("Accept(in)");
        expected.Add($"Saw(Accepted:{shipment})");
        expected.Add($"Saw(Accepted:{shipment})");
        AssertRan(run, [.. expected]);
    }

    [Fact]
    public async Task P06_local_assigned_twice_in_one_arm_with_a_read_between()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            if (await When.Flag(true))
            {
                o = await When.Accept("1");
                await Then.Saw(o);
                o = await When.Accept("2");
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(ok)", "Flag(True)", "Accept(1)", "Saw(Accepted:1)", "Accept(2)", "Saw(Accepted:2)");
    }

    // ----------------------------------------------------------------------------------------------
    // Reads between branches, sequential branches reusing a local, else-if chains of patterns.
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task P07_a_merged_local_is_read_then_reassigned_by_a_second_branch_and_read_again(bool f1, bool f2)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            if (await When.Flag({{(f1 ? "true" : "false")}}))
                o = await When.Accept("A");
            await Then.Saw(o);
            if (await When.Flag({{(f2 ? "true" : "false")}}))
                o = await When.Reject("B");
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Flag({f1})" };
        if (f1) expected.Add("Accept(A)");
        expected.Add(f1 ? "Saw(Accepted:A)" : "Saw(Accepted:S1)");
        expected.Add($"Flag({f2})");
        if (f2) expected.Add("Reject(B)");
        expected.Add(f2 ? "Saw(Rejected:B)" : (f1 ? "Saw(Accepted:A)" : "Saw(Accepted:S1)"));
        AssertRan(run, [.. expected]);
    }

    [Fact]
    public async Task P08_two_sequential_switches_reuse_a_local_and_a_pattern_variable_name()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            switch (await When.Submit("no"))
            {
                case Rejected r:
                    o = await When.Reject(r.Reason + "1");
                    break;
            }
            switch (await When.Submit("fast"))
            {
                case Accepted a when a.Express:
                    o = await When.Accept(a.Shipment + "2");
                    break;
                case Rejected r:
                    o = await When.Reject("never");
                    break;
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(ok)", "Submit(no)", "Reject(stock1)", "Submit(fast)", "Accept(S32)", "Saw(Accepted:S32)");
    }

    [Theory]
    [InlineData("no", "Rejected:stock")]
    [InlineData("ok", "Accepted:S3E")]
    [InlineData("later", "Accepted:S3E")]
    public async Task P09_an_else_if_chain_of_patterns_merges_three_ways(string key, string saw)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            if (await When.Submit("{{key}}") is Rejected r)
                o = await When.Reject(r.Reason);
            else if (await When.Submit("fast") is Accepted { Express: true } e)
                o = await When.Accept(e.Shipment + "E");
            else
                await Then.Note("none");
            await Then.Saw(o);
            """);

        var expected = new List<string> { "Submit(ok)", $"Submit({key})" };
        if (key == "no")
        {
            expected.Add("Reject(stock)");
        }
        else
        {
            expected.Add("Submit(fast)");
            expected.Add("Accept(S3E)");
        }

        expected.Add($"Saw({saw})");
        AssertRan(run, [.. expected]);
    }

    // ----------------------------------------------------------------------------------------------
    // Pattern variables: `is not` fallback, reassigned in nested branches, both arms assigning.
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("no", false, "F")]
    [InlineData("ok", true, "S1+")]
    [InlineData("ok", false, "S1")]
    public async Task P10_is_not_fallback_with_the_variable_reassigned_by_a_nested_if_in_its_own_arm(string key, bool flag, string shipment)
    {
        var run = await RunProbe($$"""
            if (await When.Submit("{{key}}") is not Accepted a)
                a = await When.Accept("F");
            else if (await When.Flag({{(flag ? "true" : "false")}}))
                a = await When.Accept(a.Shipment + "+");
            await Then.Note(a.Shipment);
            """);

        var expected = new List<string> { $"Submit({key})" };
        if (key == "no")
        {
            expected.Add("Accept(F)");
        }
        else
        {
            expected.Add($"Flag({flag})");
            if (flag) expected.Add("Accept(S1+)");
        }

        expected.Add($"Note({shipment})");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData("ok", "S1Z")]
    [InlineData("no", "F")]
    public async Task P11_is_not_with_both_arms_assigning_the_pattern_variable(string key, string shipment)
    {
        var run = await RunProbe($$"""
            if (await When.Submit("{{key}}") is not Accepted a)
                a = await When.Accept("F");
            else
                a = await When.Accept(a.Shipment + "Z");
            await Then.Note(a.Shipment);
            """);

        var expected = new List<string> { $"Submit({key})", key == "ok" ? "Accept(S1Z)" : "Accept(F)", $"Note({shipment})" };
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData("no", "Rejected:stock")]
    [InlineData("ok", "Accepted:X")]
    [InlineData("later", "Accepted:X")]
    public async Task P12_exhaustive_default_less_switch_assigning_an_unassigned_local_in_every_section(string key, string saw)
    {
        // C# accepts this: `case var other` makes the switch exhaustive, so `o` is definitely
        // assigned after it. Raun must not need a `default` to see the same.
        var body = $$"""
            Outcome o;
            switch (await When.Submit("{{key}}"))
            {
                case Rejected r:
                    o = await When.Reject(r.Reason);
                    break;
                case var other:
                    o = await When.Accept("X");
                    break;
            }
            await Then.Saw(o);
            """;
        await GeneratorHarness.DiagnoseAsync(Scenario(body), requireCompilable: true); // plain C# compiles
        var run = await RunProbe(body);

        AssertRan(run, $"Submit({key})", key == "no" ? "Reject(stock)" : "Accept(X)", $"Saw({saw})");
    }

    [Theory]
    [InlineData("no", "Rejected:stock")]
    [InlineData("ok", "Accepted:X")]
    public async Task P13_exhaustive_default_less_switch_assigning_an_unassigned_local_with_a_type_pattern_and_null(string key, string saw)
    {
        var body = $$"""
            Outcome o;
            switch (await When.Submit("{{key}}"))
            {
                case Rejected r:
                    o = await When.Reject(r.Reason);
                    break;
                case Outcome other:
                    o = await When.Accept("X");
                    break;
                case null:
                    o = await When.Accept("N");
                    break;
            }
            await Then.Saw(o);
            """;
        await GeneratorHarness.DiagnoseAsync(Scenario(body), requireCompilable: true); // plain C# compiles
        var run = await RunProbe(body);

        AssertRan(run, $"Submit({key})", key == "no" ? "Reject(stock)" : "Accept(X)", $"Saw({saw})");
    }

    // ----------------------------------------------------------------------------------------------
    // when clauses reading merged locals and outer pattern variables.
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, "big S1")]
    [InlineData(false, "small S1")]
    public async Task P14_a_when_clause_reads_a_local_merged_by_an_earlier_branch(bool flag, string note)
    {
        var run = await RunProbe($$"""
            var a = await When.Accept("s");
            if (await When.Flag({{(flag ? "true" : "false")}}))
                a = await When.Accept("sss");
            switch (await When.Submit("ok"))
            {
                case Accepted x when a.Total > 200:
                    await Then.Note("big " + x.Shipment);
                    break;
                case Accepted x:
                    await Then.Note("small " + x.Shipment);
                    break;
            }
            """);

        var expected = new List<string> { "Accept(s)", $"Flag({flag})" };
        if (flag) expected.Add("Accept(sss)");
        expected.Add("Submit(ok)");
        expected.Add($"Note({note})");
        AssertRan(run, [.. expected]);
    }

    [Fact]
    public async Task P15_a_when_clause_reads_an_outer_if_pattern_variable()
    {
        var run = await RunProbe("""
            if (await When.Submit("no") is Rejected r)
            {
                switch (await When.Submit("ok"))
                {
                    case Accepted a when r.Reason == "stock":
                        await Then.Note("A " + a.Shipment + " " + r.Reason);
                        break;
                    default:
                        await Then.Note("D");
                        break;
                }
            }
            """);

        AssertRan(run, "Submit(no)", "Submit(ok)", "Note(A S1 stock)");
    }

    [Fact]
    public async Task P16_a_when_clause_reads_an_outer_switch_pattern_variable_through_a_static_helper()
    {
        var run = await RunProbe("""
            switch (await When.Submit("big"))
            {
                case Accepted outer:
                    switch (await When.Submit("ok"))
                    {
                        case Accepted inner when Probe.IsBig(outer):
                            await Then.Note("outer big " + inner.Shipment);
                            break;
                        case Accepted inner:
                            await Then.Note("outer small " + inner.Shipment);
                            break;
                    }
                    break;
            }
            """);

        AssertRan(run, "Submit(big)", "Submit(ok)", "Note(outer big S1)");
    }

    [Fact]
    public async Task P17_a_merged_local_feeds_the_next_condition_steps_argument()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            if (await When.Flag(true))
                o = await When.Accept("M");
            switch (await When.Accept(((Accepted)o).Shipment + "2"))
            {
                case { Total: > 150 } x:
                    await Then.Note("big " + x.Shipment);
                    break;
                default:
                    await Then.Note("small");
                    break;
            }
            """);

        AssertRan(run, "Submit(ok)", "Flag(True)", "Accept(M)", "Accept(M2)", "Note(big M2)");
    }

    // ----------------------------------------------------------------------------------------------
    // Not-taken nesting: a condition sitting inside a not-taken arm, empty arms, default-less no-match.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task P18_a_condition_inside_a_not_taken_arm_leaves_its_arms_not_taken_not_skipped()
    {
        var run = await RunProbe("""
            if (await When.Flag(false))
            {
                if (await When.Submit("ok") is Accepted a)
                    await Then.Note("inner " + a.Shipment);
                else
                    await Then.Note("inner else");
            }
            await Then.Note("after");
            """);

        AssertRan(run, "Flag(False)", "Note(after)");
        AssertNotTaken(run, "Submit", "Case");
        Assert.All(run.Results.Where(r => r.Node.OperationName == "Note" && !r.DisplayName.Contains("after", StringComparison.Ordinal)),
            r => Assert.Equal(StepStatus.NotTaken, r.Status));
    }

    [Fact]
    public async Task P19_a_default_less_switch_with_locals_inside_a_not_taken_arm()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            if (await When.Flag(false))
            {
                switch (await When.Submit("no"))
                {
                    case Rejected r:
                        o = await When.Reject("x");
                        break;
                }
                await Then.Saw(o);
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(ok)", "Flag(False)", "Saw(Accepted:S1)");
        AssertNotTaken(run, "Reject");
    }

    [Fact]
    public async Task P20_default_less_no_match_whose_sections_hold_nested_branches_and_assign_a_local()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            switch (await When.Submit("later"))
            {
                case Rejected r:
                    if (await When.Flag(true))
                        o = await When.Reject("r");
                    break;
                case Accepted a:
                    switch (await When.Submit("no"))
                    {
                        case Rejected r2:
                            o = await When.Reject("r2");
                            break;
                    }
                    break;
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(ok)", "Submit(later)", "Saw(Accepted:S1)");
        AssertNotTaken(run, "Flag", "Reject");
        Assert.Equal(StepStatus.NotTaken, run.Step("Submit", "no").Status);
    }

    [Fact]
    public async Task P21_empty_arms_and_arms_holding_nothing_but_a_break()
    {
        var run = await RunProbe("""
            if (await When.Flag(true))
            {
            }
            else
            {
                await Then.Note("else");
            }
            switch (await When.Submit("ok"))
            {
                case Accepted:
                    break;
                default:
                    await Then.Note("d");
                    break;
            }
            await Then.Note("after");
            """);

        AssertRan(run, "Flag(True)", "Submit(ok)", "Note(after)");
    }

    [Theory]
    [InlineData("ok", "a S1")]
    [InlineData("later", "d")]
    [InlineData("no", "d")]
    public async Task P22_a_section_labelled_case_and_default_together(string key, string note)
    {
        var run = await RunProbe($$"""
            switch (await When.Submit("{{key}}"))
            {
                case Rejected:
                default:
                    await Then.Note("d");
                    break;
                case Accepted a:
                    await Then.Note("a " + a.Shipment);
                    break;
            }
            """);

        AssertRan(run, $"Submit({key})", $"Note({note})");
    }

    [Fact]
    public async Task P23_the_same_step_text_in_both_arms_runs_exactly_once()
    {
        var run = await RunProbe("""
            if (await When.Flag(true))
                await Then.Note("same");
            else
                await Then.Note("same");
            await Then.Note("same");
            """);

        AssertRan(run, "Flag(True)", "Note(same)", "Note(same)");
    }

    // ----------------------------------------------------------------------------------------------
    // Other condition shapes: relational/or patterns, string and bool switches, value types.
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(50, "mid")]
    [InlineData(5, "out")]
    [InlineData(100, "out")]
    public async Task P24_a_relational_and_pattern_on_a_value_type(int n, string note)
    {
        var run = await RunProbe($$"""
            if (await When.Number({{n}}) is > 10 and < 100)
                await Then.Note("mid");
            else
                await Then.Note("out");
            """);

        AssertRan(run, $"Number({n})", $"Note({note})");
    }

    [Theory]
    [InlineData("fast", "x")]
    [InlineData("no", "x")]
    [InlineData("ok", "y")]
    public async Task P25_an_or_pattern_without_variables(string key, string note)
    {
        var run = await RunProbe($$"""
            if (await When.Submit("{{key}}") is Accepted { Express: true } or Rejected)
                await Then.Note("x");
            else
                await Then.Note("y");
            """);

        AssertRan(run, $"Submit({key})", $"Note({note})");
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("b", "BC")]
    [InlineData("c", "BC")]
    [InlineData("z", "D")]
    public async Task P26_a_switch_on_a_string_result_with_constant_labels(string text, string note)
    {
        var run = await RunProbe($$"""
            switch (await When.Text("{{text}}"))
            {
                case "a":
                    await Then.Note("A");
                    break;
                case "b":
                case "c":
                    await Then.Note("BC");
                    break;
                default:
                    await Then.Note("D");
                    break;
            }
            """);

        AssertRan(run, $"Text({text})", $"Note({note})");
    }

    [Theory]
    [InlineData(true, "T")]
    [InlineData(false, "F")]
    public async Task P27_a_switch_on_a_bool_result(bool value, string note)
    {
        var run = await RunProbe($$"""
            switch (await When.Flag({{(value ? "true" : "false")}}))
            {
                case true:
                    await Then.Note("T");
                    break;
                case false:
                    await Then.Note("F");
                    break;
            }
            """);

        AssertRan(run, $"Flag({value})", $"Note({note})");
    }

    [Theory]
    [InlineData(7, "odd 7")]
    [InlineData(8, "even 8")]
    public async Task P28_a_switch_on_an_int_with_a_when_on_the_value_and_a_nested_relational_if(int n, string note)
    {
        var run = await RunProbe($$"""
            switch (await When.Number({{n}}))
            {
                case int x when x % 2 == 1:
                    await Then.Note("odd " + x);
                    break;
                case int x:
                    if (await When.Number(x * 2) is > 10)
                        await Then.Note("even " + x);
                    break;
            }
            """);

        var expected = new List<string> { $"Number({n})" };
        if (n % 2 == 0) expected.Add($"Number({n * 2})");
        expected.Add($"Note({note})");
        AssertRan(run, [.. expected]);
    }

    // ----------------------------------------------------------------------------------------------
    // Parallel groups inside arms.
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P29_a_tuple_group_inside_an_arm(bool flag)
    {
        var run = await RunProbe($$"""
            if (await When.Flag({{(flag ? "true" : "false")}}))
            {
                var (x, y) = await (When.Accept("p"), When.Accept("q"));
                await Then.Note(x.Shipment + y.Shipment);
            }
            await Then.Note("after");
            """);

        if (flag)
        {
            Assert.Equal("Flag(True)", run.Calls[0]);
            Assert.Equal("Note(pq)", run.Calls[3]);
            Assert.Equal("Note(after)", run.Calls[4]);
            AssertRanUnordered(run, "Flag(True)", "Accept(p)", "Accept(q)", "Note(pq)", "Note(after)");
        }
        else
        {
            AssertRan(run, "Flag(False)", "Note(after)");
            AssertNotTaken(run, "Accept");
        }
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("no")]
    public async Task P30_an_array_group_inside_a_switch_section_feeding_a_step_in_the_same_section(string key)
    {
        var run = await RunProbe($$"""
            switch (await When.Submit("{{key}}"))
            {
                case Accepted a:
                {
                    var parts = await new[] { When.Accept(a.Shipment + "1"), When.Accept(a.Shipment + "2") };
                    await Then.Note(parts[0].Shipment + parts[1].Shipment);
                    break;
                }
                default:
                    await Then.Note("none");
                    break;
            }
            await Then.Note("after");
            """);

        if (key == "ok")
        {
            Assert.Equal("Note(S11S12)", run.Calls[3]);
            AssertRanUnordered(run, "Submit(ok)", "Accept(S11)", "Accept(S12)", "Note(S11S12)", "Note(after)");
        }
        else
        {
            AssertRan(run, "Submit(no)", "Note(none)", "Note(after)");
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Happens-before: what follows a branch must wait for everything inside it, at any depth.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task P31_a_statement_after_nested_ifs_waits_for_the_deepest_arms_tail()
    {
        var run = await RunProbe("""
            if (await When.Flag(true))
            {
                if (await When.Flag(true))
                {
                    await When.Slow("deep");
                }
            }
            await Then.Note("after");
            """);

        AssertRan(run, "Flag(True)", "Flag(True)", "Slow(deep)", "Note(after)");
    }

    [Fact]
    public async Task P32_a_reader_of_a_merged_local_waits_for_the_arms_unrelated_tail_step()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            if (await When.Flag(true))
            {
                o = await When.Accept("a");
                await When.Slow("tail");
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(ok)", "Flag(True)", "Accept(a)", "Slow(tail)", "Saw(Accepted:a)");
    }

    [Fact]
    public async Task P33_a_statement_after_a_switch_waits_for_a_slow_step_nested_in_a_section()
    {
        var run = await RunProbe("""
            switch (await When.Submit("ok"))
            {
                case Accepted a:
                    if (await When.Flag(true))
                        await When.Slow("s");
                    break;
                default:
                    await Then.Note("d");
                    break;
            }
            await Then.Note("after");
            """);

        AssertRan(run, "Submit(ok)", "Flag(True)", "Slow(s)", "Note(after)");
    }

    [Fact]
    public async Task P34_a_statement_after_a_branch_waits_for_a_slow_step_inside_a_parallel_group_in_the_arm()
    {
        var run = await RunProbe("""
            if (await When.Flag(true))
            {
                await (When.Slow("g1"), When.Slow("g2"));
            }
            await Then.Note("after");
            """);

        Assert.Equal("Note(after)", run.Calls[^1]);
        AssertRanUnordered(run, "Flag(True)", "Slow(g1)", "Slow(g2)", "Note(after)");
    }

    // ----------------------------------------------------------------------------------------------
    // Failures: a step failing inside an arm, the condition failing, a when clause throwing, an
    // upstream failure before a branch. Downstream must be Skipped, never Passed or NotTaken-as-pass.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task P35_a_step_failing_inside_an_arm_skips_the_merge_and_everything_after()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            if (await When.Flag(true))
                o = await When.Fail("x");
            else
                o = await When.Accept("e");
            await Then.Saw(o);
            await Then.Note("after");
            """);

        Assert.Equal(["Submit(ok)", "Flag(True)", "Fail(x)"], run.Calls);
        Assert.Equal(StepStatus.Failed, run.Step("Fail").Status);
        Assert.Equal(StepStatus.NotTaken, run.Step("Accept").Status);
        Assert.Equal(StepStatus.Skipped, run.Step("Merge").Status);
        Assert.Equal(StepStatus.Skipped, run.Step("Saw").Status);
        Assert.Equal(StepStatus.Skipped, run.Step("Note").Status);
    }

    [Fact]
    public async Task P36_a_failing_condition_skips_every_arm_and_what_follows()
    {
        var run = await RunProbe("""
            if (await When.Fail("c") is Accepted a)
                await Then.Note("a " + a.Shipment);
            else
                await Then.Note("b");
            await Then.Note("after");
            """);

        Assert.Equal(["Fail(c)"], run.Calls);
        Assert.Equal(StepStatus.Failed, run.Step("Fail").Status);
        Assert.All(run.Results.Where(r => r.Node.OperationName is "Note" or "Case"),
            r => Assert.Equal(StepStatus.Skipped, r.Status));
    }

    [Fact]
    public async Task P37_a_when_clause_throwing_at_run_time_fails_the_condition_and_skips_the_arms()
    {
        var run = await RunProbe("""
            switch (await When.Submit("ok"))
            {
                case Accepted a when Probe.Boom(a):
                    await Then.Note("boom");
                    break;
                default:
                    await Then.Note("d");
                    break;
            }
            await Then.Note("after");
            """);

        Assert.Equal(["Submit(ok)"], run.Calls);
        Assert.Equal(StepStatus.Failed, run.Step("Submit").Status);
        Assert.All(run.Results.Where(r => r.Node.OperationName is "Note" or "Case"),
            r => Assert.Equal(StepStatus.Skipped, r.Status));
    }

    [Fact]
    public async Task P38_a_failure_before_a_branch_skips_the_condition_and_its_arms_rather_than_not_taking_them()
    {
        var run = await RunProbe("""
            var o = await When.Fail("x");
            if (await When.Submit("ok") is Accepted a)
                await Then.Note(a.Shipment);
            else
                await Then.Note("else");
            await Then.Saw(o);
            """);

        Assert.Equal(["Fail(x)"], run.Calls);
        Assert.DoesNotContain(run.Results, r => r.Status == StepStatus.NotTaken);
        Assert.All(run.Results.Where(r => r.Node.OperationName is "Submit" or "Note" or "Saw" or "Case"),
            r => Assert.Equal(StepStatus.Skipped, r.Status));
    }

    [Fact]
    public async Task P39_a_failure_deep_inside_a_switch_in_an_if_skips_the_outer_post_statement_and_not_takes_the_sibling_arm()
    {
        var run = await RunProbe("""
            var o = await When.Submit("ok");
            if (await When.Flag(true))
            {
                switch (await When.Submit("no"))
                {
                    case Rejected r:
                        o = await When.Fail(r.Reason);
                        break;
                    case Accepted a:
                        o = await When.Accept("never");
                        break;
                }
            }
            else
            {
                await Then.Note("else");
            }
            await Then.Saw(o);
            """);

        Assert.Equal(["Submit(ok)", "Flag(True)", "Submit(no)", "Fail(stock)"], run.Calls);
        Assert.Equal(StepStatus.Failed, run.Step("Fail").Status);
        Assert.Equal(StepStatus.NotTaken, run.Step("Accept").Status);
        Assert.Equal(StepStatus.NotTaken, run.Step("Note").Status);
        Assert.Equal(StepStatus.Skipped, run.Step("Saw").Status);
        Assert.DoesNotContain(run.Results, r => r.Node.OperationName == "Saw" && r.Status == StepStatus.Passed);
    }

    // ----------------------------------------------------------------------------------------------
    // Shapes C# accepts that Raun may refuse: they must be refused with a diagnostic, never lowered
    // into something that runs differently.
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task P40_assigning_a_local_from_another_local_inside_an_arm_is_refused_or_correct()
    {
        var source = Scenario("""
            var o = await When.Submit("ok");
            var p = await When.Submit("no");
            if (await When.Flag(true))
                o = p;
            await Then.Saw(o);
            """);

        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        if (diagnostics.IsEmpty)
        {
            var result = GeneratorHarness.Run(source);
            result.AssertCompiles();
            var results = await Assert.Single(result.Definitions()).RunAsync();
            var calls = (ConcurrentQueue<string>)result.Assembly!.GetType("ProbeDemo.Trace")!.GetField("Calls")!.GetValue(null)!;
            AssertRan(new ProbeRun(results, [.. calls], result.GeneratedSource), "Submit(ok)", "Submit(no)", "Flag(True)", "Saw(Rejected:stock)");
        }
        else
        {
            Assert.All(diagnostics, d => Assert.StartsWith("RAUN", d.Id, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("no", "Rejected:stock")]
    [InlineData("ok", "Accepted:X")]
    public async Task P41_exhaustive_default_less_switch_reassigning_a_local_defined_before_it(string key, string saw)
    {
        // Same as P12 but the local has a value before the switch: the implicit no-match arm can never
        // run, yet it is one more (pass-through) merge source. Must still pick the section's value.
        var run = await RunProbe($$"""
            var o = await When.Submit("later");
            switch (await When.Submit("{{key}}"))
            {
                case Rejected r:
                    o = await When.Reject(r.Reason);
                    break;
                case var other:
                    o = await When.Accept("X");
                    break;
            }
            await Then.Saw(o);
            """);

        AssertRan(run, "Submit(later)", $"Submit({key})", key == "no" ? "Reject(stock)" : "Accept(X)", $"Saw({saw})");
    }

    [Theory]
    [InlineData("ok", "S1F")]
    [InlineData("no", "F")]
    public async Task P42_a_pattern_variable_bound_in_the_else_arm_feeds_a_nested_conditions_argument(string key, string shipment)
    {
        var run = await RunProbe($$"""
            if (await When.Submit("{{key}}") is not Accepted a)
                a = await When.Accept("F");
            else if (await When.Accept(a.Shipment + "F") is { Express: false } b)
                a = await When.Accept(b.Shipment);
            await Then.Note(a.Shipment);
            """);

        var expected = new List<string> { $"Submit({key})" };
        if (key == "ok")
        {
            expected.Add("Accept(S1F)");
            expected.Add("Accept(S1F)");
        }
        else
        {
            expected.Add("Accept(F)");
        }

        expected.Add($"Note({shipment})");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData(true, "big S1")]
    [InlineData(false, "small S1")]
    public async Task P43_a_when_clause_reads_a_local_merged_by_an_earlier_branch_inside_the_same_arm(bool flag, string note)
    {
        var run = await RunProbe($$"""
            var a = await When.Accept("s");
            if (await When.Flag(true))
            {
                if (await When.Flag({{(flag ? "true" : "false")}}))
                    a = await When.Accept("sss");
                switch (await When.Submit("ok"))
                {
                    case Accepted x when a.Total > 200:
                        await Then.Note("big " + x.Shipment);
                        break;
                    case Accepted x:
                        await Then.Note("small " + x.Shipment);
                        break;
                }
            }
            await Then.Note(a.Shipment);
            """);

        var expected = new List<string> { "Accept(s)", "Flag(True)", $"Flag({flag})" };
        if (flag) expected.Add("Accept(sss)");
        expected.Add("Submit(ok)");
        expected.Add($"Note({note})");
        expected.Add(flag ? "Note(sss)" : "Note(s)");
        AssertRan(run, [.. expected]);
    }

    [Theory]
    [InlineData(true, true, "Accepted:ii")]
    [InlineData(true, false, "Accepted:io")]
    [InlineData(false, true, "Accepted:ei")]
    [InlineData(false, false, "Rejected:eo")]
    public async Task P44_both_outer_arms_hold_nested_ifs_that_each_reassign_the_local(bool outer, bool inner, string saw)
    {
        var run = await RunProbe($$"""
            var o = await When.Submit("ok");
            if (await When.Flag({{(outer ? "true" : "false")}}))
            {
                if (await When.Flag({{(inner ? "true" : "false")}}))
                    o = await When.Accept("ii");
                else
                    o = await When.Accept("io");
            }
            else
            {
                if (await When.Flag({{(inner ? "true" : "false")}}))
                    o = await When.Accept("ei");
                else
                    o = await When.Reject("eo");
            }
            await Then.Saw(o);
            """);

        var step = (outer, inner) switch
        {
            (true, true) => "Accept(ii)",
            (true, false) => "Accept(io)",
            (false, true) => "Accept(ei)",
            _ => "Reject(eo)",
        };
        AssertRan(run, "Submit(ok)", $"Flag({outer})", $"Flag({inner})", step, $"Saw({saw})");
    }
}
