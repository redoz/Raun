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
