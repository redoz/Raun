using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A scenario body is written as C# and read by the generator, never run. Whatever the generator
/// accepts must therefore mean what C# says it means; a shape it cannot honour has to be refused
/// with a diagnostic, because the alternative is a green build that runs something else.
/// </summary>
public class ScenarioBodySemanticsTests
{
    [Theory]
    [InlineData("var total = await Given.Three(); total += await Given.Four(); await Given.Record(total);")]
    [InlineData("var text = await Given.Text(); text ??= await Given.Other(); await Given.Record(text);")]
    public void A_compound_assignment_of_a_step_result_is_refused(string body)
    {
        var result = GeneratorHarness.Run(Source(body));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "RAUN002");
        Assert.DoesNotContain("Story", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void A_using_declaration_of_a_step_result_is_refused_and_points_at_teardown()
    {
        var result = GeneratorHarness.Run(Source("using var opened = await Given.Open(); await Given.Record(opened);"));

        var refused = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "RAUN017");
        Assert.Contains("OnTeardown", refused.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("double half = await Given.Three(); await Given.Record(half / 2);", "half")]
    [InlineData("long n; if (await Given.Flag()) { n = await Given.Three(); } else { n = await Given.Four(); } await Given.Record(n);", "n")]
    [InlineData("(long a, var b) = await (Given.Three(), Given.Four()); await Given.Record(a + b);", "a")]
    public void A_local_whose_type_would_convert_the_step_result_is_refused(string body, string local)
    {
        var result = GeneratorHarness.Run(Source(body));

        // One refusal per assignment that would convert, so an if/else reports both arms.
        var refusals = result.GeneratorDiagnostics.Where(d => d.Id == "RAUN017").ToList();
        Assert.NotEmpty(refusals);
        Assert.All(refusals, refused => Assert.Contains(
            "'" + local + "'", refused.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.DoesNotContain("Story", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IAnimal animal = await Given.ADog(); await Given.Check(animal);", "IAnimal")]
    [InlineData("var dog = await Given.ADog(); await Given.Check(dog);", "Dog")]
    [InlineData("IAnimal animal; if (await Given.Flag()) { animal = await Given.ADog(); } else { animal = await Given.ACat(); } await Given.Check(animal);", "IAnimal")]
    [InlineData("int? maybe = await Given.Three(); await Given.Record(maybe);", "Int32:3")]
    [InlineData("object boxed = await Given.Three(); await Given.Record(boxed);", "Int32:3")]
    public async Task A_step_result_is_read_as_the_type_its_local_declares(string body, string expected)
    {
        var result = GeneratorHarness.Run(Source(body));
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.DoesNotContain(results, step => step.Status == StepStatus.Failed);
        Assert.Equal([expected], Seen(result));
    }

    [Fact]
    public void A_scenario_case_that_does_not_bind_is_reported_by_the_generator_itself()
    {
        // The analyzer may be switched off (RunAnalyzers=false); the scenario must not vanish with it.
        var source = Source("await Given.Record(count);")
            .Replace("[Scenario] public async Task Story()", "[Scenario, ScenarioCase(\"one\", 1), ScenarioCase(\"bad\", \"x\")] public async Task Story(int count)", StringComparison.Ordinal);

        var result = GeneratorHarness.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "RAUN034");
    }

    /// <summary>
    /// The differential check: the same body is compiled twice into one assembly, once as a
    /// [Scenario] the generator lowers and once as an ordinary method the C# compiler runs as
    /// written. Every step records its call and arguments. The generator may refuse a body, with a
    /// diagnostic; what it accepts must record exactly what C# records.
    /// </summary>
    [Theory]
    [InlineData("var a = await Given.Three(); var b = await Given.Add(a, 4); await Given.Record(b);")]
    [InlineData("var a = await Given.Three(); a = await Given.Add(a, 1); a = await Given.Add(a, a); await Given.Record(a);")]
    [InlineData("var a = await Given.Three(); await Given.Record(a * 2 + 1); await Given.Record($\"v={a}\"); await Given.Record(a / 2);")]
    [InlineData("int n; if (await Given.Flag(true)) { n = await Given.Three(); } else { n = await Given.Four(); } await Given.Record(n);")]
    [InlineData("int n; if (await Given.Flag(false)) { n = await Given.Three(); } else { n = await Given.Four(); } await Given.Record(n);")]
    [InlineData("var a = await Given.Three(); if (await Given.Flag(true)) { a = await Given.Add(a, 10); } await Given.Record(a);")]
    [InlineData("var a = await Given.Three(); if (await Given.Flag(false)) { a = await Given.Add(a, 10); } await Given.Record(a);")]
    [InlineData("var a = await Given.Three(); if (await Given.Flag(true)) { if (await Given.Flag(false)) { a = await Given.Add(a, 1); } else { a = await Given.Add(a, 2); } } await Given.Record(a);")]
    [InlineData("var (a, b) = await (Given.Three(), Given.Four()); await Given.Record(a - b);")]
    [InlineData("IAnimal animal = await Given.ADog(); await Given.Check(animal);")]
    [InlineData("var dog = await Given.ADog(); await Given.Check(dog);")]
    [InlineData("IAnimal animal; if (await Given.Flag(false)) { animal = await Given.ADog(); } else { animal = await Given.ACat(); } await Given.Check(animal);")]
    [InlineData("int? maybe = await Given.Three(); await Given.Record(maybe);")]
    [InlineData("object boxed = await Given.Three(); await Given.Record(boxed);")]
    [InlineData("double half = await Given.Three(); await Given.Record(half / 2);")]
    [InlineData("long wide = await Given.Three(); await Given.Record(wide);")]
    [InlineData("var total = await Given.Three(); total += await Given.Four(); await Given.Record(total);")]
    [InlineData("var total = await Given.Three(); total -= await Given.Four(); await Given.Record(total);")]
    [InlineData("var text = await Given.Text(); text ??= await Given.Text(); await Given.Record(text);")]
    [InlineData("var a = await Given.Three(); var b = a; await Given.Record(b);")]
    [InlineData("var a = await Given.Three(); await Given.Record(a); await Given.Record(a.ToString().Length);")]
    [InlineData("var d = await Given.ADog(); await Given.Record(d is IAnimal); await Given.Record(d?.GetType().Name);")]
    [InlineData("var a = await Given.Add(1, 2); var b = await Given.Add(a, await Given.Three()); await Given.Record(b);")]
    [InlineData("await Given.Record(await Given.Three());")]
    [InlineData("var a = await Given.Three(); await Given.Equal(a + 1, 4);")]
    [InlineData("var a = await Given.Three(); await Given.Equal(expected: 3, actual: a);")]
    [InlineData("var a = await Given.Three(); await Given.Equal(a, 3, \"said by hand\");")]
    [InlineData("await Given.Equal( 1 +  2 , 3);")]
    [InlineData("await Given.Named(1);")]
    public async Task What_the_generator_accepts_runs_what_CSharp_runs(string body)
    {
        var result = GeneratorHarness.Run(OracleSource(body));
        Assert.True(
            result.EmitDiagnostics.IsEmpty,
            "the body is not valid C#, or the generated code does not compile: " + string.Join("; ", result.EmitDiagnostics));
        if (!result.GeneratorDiagnostics.IsEmpty)
        {
            // Refused out loud, with nothing generated for it: an honest outcome.
            Assert.DoesNotContain("Story", result.GeneratedSource, StringComparison.Ordinal);
            return;
        }

        var trace = (List<string>)result.Assembly!.GetType("Oracle")!.GetField("Calls")!.GetValue(null)!;

        var plain = result.Assembly.GetType("Plain")!;
        await (Task)plain.GetMethod("Story")!.Invoke(Activator.CreateInstance(plain), null)!;
        var expected = trace.ToList();
        trace.Clear();

        var results = await result.Definitions().Single().RunAsync(maxParallelism: 1);
        Assert.DoesNotContain(results, step => step.Status is StepStatus.Failed or StepStatus.Skipped);

        Assert.Equal(expected, trace);
    }

    private static string OracleSource(string body) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Raun;
        public static class Oracle
        {
            public static readonly List<string> Calls = new();
            public static void Add(FormattableString call) { lock (Calls) { Calls.Add(FormattableString.Invariant(call)); } }
        }
        public interface IAnimal { }
        public sealed class Dog : IAnimal { }
        public sealed class Cat : IAnimal { }
        public sealed class OracleSteps : Given<NoWorld>
        {
            public Task<int> Three() { Oracle.Add($"Three()"); return Task.FromResult(3); }
            public Task<int> Four() { Oracle.Add($"Four()"); return Task.FromResult(4); }
            public Task<int> Add(int left, int right) { Oracle.Add($"Add({left}, {right})"); return Task.FromResult(left + right); }
            public Task<bool> Flag(bool value) { Oracle.Add($"Flag({value})"); return Task.FromResult(value); }
            public Task<string?> Text() { Oracle.Add($"Text()"); return Task.FromResult<string?>("text"); }
            public Task<Dog> ADog() { Oracle.Add($"ADog()"); return Task.FromResult(new Dog()); }
            public Task<Cat> ACat() { Oracle.Add($"ACat()"); return Task.FromResult(new Cat()); }
            public Task Record(object? value) { Oracle.Add($"Record({value?.GetType().Name}:{value})"); return Task.CompletedTask; }
            public Task Check(IAnimal animal) { Oracle.Add($"Check(IAnimal {animal.GetType().Name})"); return Task.CompletedTask; }
            public Task Check(Dog dog) { Oracle.Add($"Check(Dog)"); return Task.CompletedTask; }
            public Task Equal(int actual, int expected,
                [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(actual))] string? written = null,
                [System.Runtime.CompilerServices.CallerMemberName] string? member = null)
            { Oracle.Add($"Equal({actual}, {expected}, '{written}', in {member})"); return Task.CompletedTask; }
            public Task Named(int value,
                [System.Runtime.CompilerServices.CallerMemberName] string @in = "",
                [System.Runtime.CompilerServices.CallerArgumentExpression("missing")] string? nothing = null,
                [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? written = null)
            { Oracle.Add($"Named({value}, in {@in}, '{nothing}', '{written}')"); return Task.CompletedTask; }
        }
        public sealed class Stories : Scenarios<NoWorld>
        {
            public OracleSteps Given => Steps<OracleSteps>();
            [Scenario] public async Task Story()
            {
                {{body}}
            }
        }
        public sealed class Plain
        {
            public OracleSteps Given { get; } = new OracleSteps();
            public async Task Story()
            {
                {{body}}
            }
        }
        """;

    private static List<string> Seen(GeneratorResult result)
        => (List<string>)result.Assembly!.GetType("Probe")!.GetField("Seen")!.GetValue(null)!;

    private static string Source(string body) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Raun;
        public static class Probe { public static readonly List<string> Seen = new(); }
        public interface IAnimal { }
        public sealed class Dog : IAnimal { }
        public sealed class Cat : IAnimal { }
        public sealed class Opened : IDisposable { public void Dispose() { } }
        public sealed class Steps : Given<NoWorld>
        {
            public Task<int> Three() => Task.FromResult(3);
            public Task<int> Four() => Task.FromResult(4);
            public Task<bool> Flag() => Task.FromResult(true);
            public Task<string> Text() => Task.FromResult("text");
            public Task<string> Other() => Task.FromResult("other");
            public Task<Dog> ADog() => Task.FromResult(new Dog());
            public Task<Cat> ACat() => Task.FromResult(new Cat());
            public Task<Opened> Open() => Task.FromResult(new Opened());
            public Task Record(object? value)
            {
                Probe.Seen.Add(FormattableString.Invariant($"{value?.GetType().Name}:{value}"));
                return Task.CompletedTask;
            }
            public Task Check(IAnimal animal) { Probe.Seen.Add("IAnimal"); return Task.CompletedTask; }
            public Task Check(Dog dog) { Probe.Seen.Add("Dog"); return Task.CompletedTask; }
        }
        public sealed class Stories : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task Story()
            {
                {{body}}
            }
        }
        """;
}
