using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Generator.Test;

public class StepArgumentEvaluationTests
{
    [Fact]
    public async Task Title_claim_and_call_share_one_evaluated_argument()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(Source));
        var definition = Assert.Single(generated.Definitions());
        var results = await definition.RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var use = Assert.Single(results, result => result.Node.OperationName == "Use");
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Equal("1", Read<string>(generated, "Actual"));
        Assert.Equal("Given Patient:1 / 1", use.DisplayName);
        Assert.Equal("1", Assert.Single(use.Effects).Identity.Key.Value);
    }

    [Fact]
    public async Task A_throwing_argument_is_evaluated_once_and_fails_its_consuming_step()
    {
        var source = Source.Replace("return (++Counter).ToString();", "++Counter; throw new InvalidOperationException(\"factory failed\");", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        var use = Assert.Single(results, result => result.Node.OperationName == "Use");
        Assert.Equal(StepStatus.Failed, use.Status);
        Assert.Equal("factory failed", Assert.IsType<InvalidOperationException>(use.Exception).Message);
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Equal("", Read<string>(generated, "Actual"));
        Assert.Empty(use.Effects);
    }

    [Fact]
    public async Task Skipped_steps_do_not_evaluate_argument_factories()
    {
        var source = Source.Replace("await Given.Use(ProbeState.Next());", "await Given.Fail(); await Given.Use(ProbeState.Next());", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var definition = Assert.Single(generated.Definitions());
        Assert.Equal(0, Read<int>(generated, "Counter"));
        var results = await definition.RunAsync();
        Assert.Equal(StepStatus.Skipped, Assert.Single(results, result => result.Node.OperationName == "Use").Status);
        Assert.Equal(0, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Named_arguments_keep_authored_order_and_parameter_binding()
    {
        var source = Source.Replace("{key} / {key:id}", "{key} / {other} / {key:id}", StringComparison.Ordinal)
            .Replace("string key)", "string key, string other)", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = key + \":\" + other;", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(other: ProbeState.Next(), key: ProbeState.Next())", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal(2, Read<int>(generated, "Counter"));
        Assert.Equal("2:1", Read<string>(generated, "Actual"));
        Assert.Equal("Given Patient:2 / 1 / 2", use.DisplayName);
        Assert.Equal("2", Assert.Single(use.Effects).Identity.Key.Value);
    }

    [Fact]
    public async Task Implicit_argument_conversions_are_evaluated_once()
    {
        var source = Source.Replace("public static string Next() { return (++Counter).ToString(); }",
            "public static Key Next() { ++Counter; return new Key(); } public static int Conversions;", StringComparison.Ordinal)
            + "public sealed class Key { public static implicit operator string(Key value) => (++ProbeState.Conversions).ToString(); }";
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Equal(1, Read<int>(generated, "Conversions"));
        Assert.Equal("1", Read<string>(generated, "Actual"));
        Assert.Equal("Given Patient:1 / 1", use.DisplayName);
        Assert.Equal("1", Assert.Single(use.Effects).Identity.Key.Value);
    }

    [Fact]
    public async Task Object_factory_values_are_shared_with_return_lineage_and_completed_titles()
    {
        var source = Source.Replace("public static string Next() { return (++Counter).ToString(); }",
            "public static Patient? Expected; public static Patient Next() => Expected = new Patient((++Counter).ToString());", StringComparison.Ordinal)
            .Replace("[StepName(\"Given {key} / {key:id}\")]", "[StepName(\"Given {$return ?? key} / {key:id}\")] [return: Created(References = [nameof(key)])]", StringComparison.Ordinal)
            .Replace("public Task Use([Read, ResourceKey<Patient>] string key)", "public Task<Patient> Use([Read] Patient key)", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "if (!ReferenceEquals(ProbeState.Expected, key)) throw new InvalidOperationException(\"different object\"); ProbeState.Actual = key.Id;", StringComparison.Ordinal)
            .Replace("return Task.CompletedTask;", "return Task.FromResult(new Patient(\"created\"));", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Equal("1", Read<string>(generated, "Actual"));
        Assert.Equal("Given Patient:created / 1", use.DisplayName);
        var read = Assert.Single(use.Effects, effect => effect.Verb == LifecycleVerb.Reference);
        Assert.Equal(read.Identity, Assert.Single(use.Lineage).Target);
        Assert.Equal(read.Identity, use.TitleMessage.Parts.Last(part => part.Resource.HasValue).Resource);
    }

    [Fact]
    public async Task Unselected_steps_do_not_evaluate_argument_factories()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var results = await new ScenarioScheduler().RunAsync(Assert.Single(generated.Definitions()), targets: new HashSet<int> { 0 });
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(0, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Not_taken_steps_do_not_evaluate_argument_factories()
    {
        var source = Source.Replace("public Task Fail()", "public Task<bool> Check() => Task.FromResult(false); public Task Fail()", StringComparison.Ordinal)
            .Replace("await Given.Use(ProbeState.Next());", "if (await Given.Check()) { await Given.Use(ProbeState.Next()); }", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.Equal(StepStatus.NotTaken, Assert.Single(results, result => result.Node.OperationName == "Use").Status);
        Assert.Equal(0, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Reused_definitions_capture_fresh_arguments_for_each_run()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definition = Assert.Single(generated.Definitions());
        var first = (await definition.RunAsync())[1];
        var second = (await definition.RunAsync())[1];
        Assert.Equal(StepStatus.Passed, first.Status);
        Assert.Equal(StepStatus.Passed, second.Status);
        Assert.Equal("1", Assert.Single(first.Effects).Identity.Key.Value);
        Assert.Equal("2", Assert.Single(second.Effects).Identity.Key.Value);
        Assert.Equal("Given Patient:1 / 1", first.DisplayName);
        Assert.Equal("Given Patient:2 / 2", second.DisplayName);
        Assert.Equal(2, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Concurrent_runs_of_one_definition_have_independent_captures()
    {
        var source = Source.Replace("(++Counter)", "System.Threading.Interlocked.Increment(ref Counter)", StringComparison.Ordinal)
            .Replace("public Task Use(", "public async Task Use(", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "await Task.Yield(); ProbeState.Actual = key;", StringComparison.Ordinal)
            .Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var definition = Assert.Single(generated.Definitions());
        var runs = await Task.WhenAll(definition.RunAsync(), definition.RunAsync());
        Assert.Equal(2, Read<int>(generated, "Counter"));
        Assert.Equal(new[] { "1", "2" }, runs.Select(results => Assert.Single(results[1].Effects).Identity.Key.Value).Order(StringComparer.Ordinal));
        foreach (var results in runs)
        {
            var use = results[1];
            Assert.Equal(StepStatus.Passed, use.Status);
            var key = Assert.Single(use.Effects).Identity.Key.Value;
            Assert.Equal($"Given Patient:{key} / {key}", use.DisplayName);
        }
    }

    [Fact]
    public async Task Contextual_lambdas_remain_bound_to_the_parameter_type()
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "Func<string> factory", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = factory();", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(() => ProbeState.Next())", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Equal(StepStatus.Passed, (await Assert.Single(generated.Definitions()).RunAsync())[1].Status);
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Equal("1", Read<string>(generated, "Actual"));
    }

    [Fact]
    public async Task Expanded_params_arguments_preserve_evaluation_order()
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "params string[] keys", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = string.Join(\":\", keys);", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ProbeState.Next(), ProbeState.Next())", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Equal(StepStatus.Passed, (await Assert.Single(generated.Definitions()).RunAsync())[1].Status);
        Assert.Equal(2, Read<int>(generated, "Counter"));
        Assert.Equal("1:2", Read<string>(generated, "Actual"));
    }

    [Fact]
    public async Task Anonymous_arguments_keep_generic_type_inference()
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "", StringComparison.Ordinal)
            .Replace("public Task Use([Read, ResourceKey<Patient>] string key)", "public Task Use<T>(T key)", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = key!.ToString()!;", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(new { Id = ProbeState.Next() })", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        Assert.Equal(StepStatus.Passed, (await Assert.Single(generated.Definitions()).RunAsync())[1].Status);
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Equal("{ Id = 1 }", Read<string>(generated, "Actual"));
    }

    [Fact]
    public async Task Capture_preserves_nested_nullable_parameter_annotations()
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "[StepName(\"Given {key}\")]", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "System.Collections.Generic.IEnumerable<string?> key", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = System.Linq.Enumerable.Single(key) ?? \"null\";", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(new string?[] { null })", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        Assert.Equal(StepStatus.Passed, (await Assert.Single(generated.Definitions()).RunAsync())[1].Status);
        Assert.Equal("null", Read<string>(generated, "Actual"));
    }

    [Fact]
    public async Task Argument_factories_run_inside_the_consuming_step_context()
    {
        var source = Source.Replace("return (++Counter).ToString();",
            "ScenarioContext.Current!.Log(\"argument prepared\"); return (++Counter).ToString();", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal(1, Read<int>(generated, "Counter"));
        Assert.Contains(use.LogEntries, entry => entry.Message == "argument prepared");
        Assert.Equal("Given Patient:1 / 1", Assert.Single(use.Effects).StepDisplayName);
    }

    [Fact]
    public async Task Starting_and_finishing_observer_callbacks_remain_serial()
    {
        var source = Source.Replace("await Given.Use(ProbeState.Next());",
            "await (Given.Use(ProbeState.Next()), Given.Use(ProbeState.Next()));", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var observer = new SerialObserver();
        var results = await new ScenarioScheduler().RunAsync(Assert.Single(generated.Definitions()), observer: observer);
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        Assert.Equal(1, observer.MaxActive);
        Assert.Equal(results.Count * 2, observer.Callbacks);
    }

    private sealed class SerialObserver : IStepObserver
    {
        private int _active;
        public int MaxActive { get; private set; }
        public int Callbacks { get; private set; }
        public Task OnStepStartingAsync(StepContext context) => RecordAsync();
        public Task OnStepFinishedAsync(StepResult result) => RecordAsync();
        private async Task RecordAsync()
        {
            var active = Interlocked.Increment(ref _active);
            MaxActive = Math.Max(MaxActive, active);
            Callbacks++;
            await Task.Delay(10);
            Interlocked.Decrement(ref _active);
        }
    }

    [Theory]
    [InlineData("string?[]", "new string?[] { null }")]
    [InlineData("string?[]?[]", "new string?[]?[] { null, new string?[] { null } }")]
    [InlineData("(string? Name, string? Value)", "(Name: (string?)null, Value: (string?)null)")]
    public async Task Nested_nullable_arrays_and_tuples_compile_cleanly(string type, string expression)
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "[StepName(\"Given {key}\")]", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", type + " key", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = \"called\";", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(" + expression + ")", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        Assert.Equal(StepStatus.Passed, (await Assert.Single(generated.Definitions()).RunAsync())[1].Status);
        Assert.Equal("called", Read<string>(generated, "Actual"));
    }

    [Theory]
    [InlineData("ref string key", "ref ProbeState.Actual", "key = \"changed\";")]
    [InlineData("out string key", "out ProbeState.Actual", "key = \"changed\";")]
    [InlineData("in string key", "in ProbeState.Actual", "ProbeState.Actual = \"changed\";")]
    [InlineData("ReadOnlySpan<char> key", "ProbeState.Next().AsSpan()", "ProbeState.Actual = key.ToString();")]
    public async Task Unobserved_arguments_preserve_by_reference_and_ref_struct_calls(string parameter, string argument, string body)
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", parameter, StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", body, StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(" + argument + ")", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var definition = Assert.Single(generated.Definitions());
        Assert.Null(definition.Nodes[1].PrepareArguments);
        Assert.Equal(StepStatus.Passed, (await definition.RunAsync())[1].Status);
        Assert.Equal(parameter.StartsWith("ReadOnlySpan", StringComparison.Ordinal) ? "1" : "changed", Read<string>(generated, "Actual"));
    }

    [Theory]
    [InlineData("ref", "key = \"changed\";", "changed")]
    [InlineData("in", "", "before")]
    [InlineData("out", "key = \"changed\";", "changed")]
    public async Task Metadata_preserves_by_reference_locations(string modifier, string body, string expected)
    {
        var source = Source.Replace("public static string Actual = \"\";", "public static string Actual = \"before\";", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", modifier + " string key", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", body, StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(" + modifier + " ProbeState.Actual)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given before / before", use.DisplayName);
        Assert.Equal(expected, Read<string>(generated, "Actual"));
    }

    [Fact]
    public async Task Mixed_span_arguments_preserve_authored_order_and_observed_values()
    {
        var source = Source.Replace("[StepName(\"Given {key} / {key:id}\")]", "[StepName(\"Given {key} / {key}\")]", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "ReadOnlySpan<char> span, string key", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = span.ToString() + \":\" + key;", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ProbeState.Next().AsSpan(), ProbeState.Next())", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given 2 / 2", use.DisplayName);
        Assert.Equal("1:2", Read<string>(generated, "Actual"));
        Assert.Equal(2, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task By_reference_argument_locations_are_evaluated_once()
    {
        var source = Source.Replace("public static string Next() { return (++Counter).ToString(); }",
            "public static string[] Values = [\"before\"]; public static string[] NextArray() { ++Counter; return Values; }", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "[Read, ResourceKey<Patient>] ref string key", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = key; key = \"changed\";", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ref ProbeState.NextArray()[0])", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given Patient:before / before", use.DisplayName);
        Assert.Equal("before", Assert.Single(use.Effects).Identity.Key.Value);
        Assert.Equal("before", Read<string>(generated, "Actual"));
        Assert.Equal("changed", Assert.Single(Read<string[]>(generated, "Values")));
        Assert.Equal(1, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Return_lineage_uses_the_post_call_ref_value_without_re_evaluating_its_location()
    {
        var source = Source.Replace("public static string Next() { return (++Counter).ToString(); }",
            "public static string[] Values = [\"before\"]; public static string[] NextArray() { ++Counter; return Values; }", StringComparison.Ordinal)
            .Replace("public Task Use([Read, ResourceKey<Patient>] string key)",
                "[return: Created(References = [nameof(key)])] public Task<Patient> Use([ResourceKey<Patient>] ref string key)", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = key; key = \"after\";", StringComparison.Ordinal)
            .Replace("return Task.CompletedTask;", "return Task.FromResult(new Patient(\"created\"));", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ref ProbeState.NextArray()[0])", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given Patient:before / before", use.DisplayName);
        Assert.Equal("after", Assert.Single(use.Lineage).Target.Key.Value);
        Assert.Equal(1, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Mixed_out_discards_remain_valid_with_observed_value_arguments()
    {
        var source = Source.Replace("[Read, ResourceKey<Patient>] string key", "string key, out int discarded", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "discarded = 7; ProbeState.Actual = key;", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ProbeState.Next(), out _)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given 1 / 1", use.DisplayName);
        Assert.Equal(1, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Mixed_out_declarations_remain_valid_with_observed_value_arguments()
    {
        var source = Source.Replace("[Read, ResourceKey<Patient>] string key", "string key, out int discarded", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "discarded = 7; ProbeState.Actual = key;", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ProbeState.Next(), out var ignored)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given 1 / 1", use.DisplayName);
        Assert.Equal(1, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Omitted_in_modifier_keeps_an_addressable_arguments_alias()
    {
        var source = Source.Replace("public static string Actual = \"\";", "public static string Actual = \"before\"; public static string Seen = \"\";", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "[Read, ResourceKey<Patient>] in string key", StringComparison.Ordinal)
            .Replace("ProbeState.Actual = key;", "ProbeState.Actual = \"changed\"; ProbeState.Seen = key;", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ProbeState.Actual)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given Patient:before / before", use.DisplayName);
        Assert.Equal("before", Assert.Single(use.Effects).Identity.Key.Value);
        Assert.Equal("changed", Read<string>(generated, "Seen"));
    }

    [Theory]
    [InlineData("var holder = await Given.Produce(); await Given.Use(holder.Key);")]
    [InlineData("await Given.Use(ProbeState.Make().Key);")]
    public async Task Implicit_in_fields_on_returned_struct_values_keep_temporary_semantics(string statement)
    {
        var source = Source.Replace("public static string Next() { return (++Counter).ToString(); }",
            "public static string Next() { return (++Counter).ToString(); } public static Holder Make() => new Holder(Next());", StringComparison.Ordinal)
            .Replace("public Task Fail()", "public Task<Holder> Produce() => Task.FromResult(ProbeState.Make()); public Task Fail()", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "[Read, ResourceKey<Patient>] in string key", StringComparison.Ordinal)
            .Replace("await Given.Use(ProbeState.Next());", statement, StringComparison.Ordinal)
            + "public struct Holder { public string Key; public Holder(string key) { Key = key; } }";
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var use = Assert.Single(await Assert.Single(generated.Definitions()).RunAsync(), result => result.Node.OperationName == "Use");
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("Given Patient:1 / 1", use.DisplayName);
        Assert.Equal("1", Read<string>(generated, "Actual"));
        Assert.Equal(1, Read<int>(generated, "Counter"));
    }

    [Fact]
    public async Task Capturing_a_dynamic_parameter_preserves_static_overload_selection()
    {
        var source = Source.Replace("public static string Next()", "public static object Next()", StringComparison.Ordinal)
            .Replace("[StepName(\"Given {key} / {key:id}\")]", "[StepName(\"Given {name}\")]", StringComparison.Ordinal)
            .Replace("[Read, ResourceKey<Patient>] string key", "dynamic key, string name", StringComparison.Ordinal)
            .Replace("Given.Use(ProbeState.Next())", "Given.Use(ProbeState.Next(), \"selected\")", StringComparison.Ordinal)
            .Replace("public Task Fail()", "public Task Use(string key, string name) => throw new InvalidOperationException(\"wrong overload\"); public Task Fail()", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var use = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, use.Status);
        Assert.Equal("1", Read<string>(generated, "Actual"));
        Assert.Equal(1, Read<int>(generated, "Counter"));
    }

    private static T Read<T>(GeneratorResult generated, string field)
        => (T)generated.Assembly!.GetType("ProbeState")!.GetField(field)!.GetValue(null)!;

    private const string Source = """
        using System;
        using System.Threading.Tasks;
        using Raun;
        public static class ProbeState
        {
            public static int Counter;
            public static string Actual = "";
            public static string Next() { return (++Counter).ToString(); }
        }
        public sealed record Patient(string Id) : IResource<Patient>
        {
            public static ResourceKey KeyFor(Patient patient) => patient.Id;
        }
        public sealed class ProbeGiven : Given<NoWorld>
        {
            [StepName("Given {key} / {key:id}")]
            public Task Use([Read, ResourceKey<Patient>] string key)
            {
                ProbeState.Actual = key;
                return Task.CompletedTask;
            }
            public Task Fail() => throw new InvalidOperationException("predecessor failed");
        }
        public sealed class ProbeScenarios : Scenarios<NoWorld>
        {
            public ProbeGiven Given => Steps<ProbeGiven>();
            [Scenario] public async Task Probe() { await Given.Use(ProbeState.Next()); }
        }
        """;
}
