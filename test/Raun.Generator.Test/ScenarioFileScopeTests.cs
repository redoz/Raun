using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A step argument moves from the scenario's file into a generated file in another namespace. What
/// was in scope where the scenario wrote it has to be in scope there too: the scenario's own
/// namespace and its parents, and the using directives of every namespace block around it, not only
/// the ones at the top of the file. An extension method is the case that shows it, because it is
/// found through scope alone.
/// </summary>
public class ScenarioFileScopeTests
{
    private const string Dsl = """
        namespace Dsl
        {
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Raun;
            public static class Probe { public static readonly List<string> Seen = new(); }
            public sealed class Steps : Given<NoWorld>
            {
                public Task<int> Three() => Task.FromResult(3);
                public Task Record(object? value)
                {
                    Probe.Seen.Add(System.FormattableString.Invariant($"{value}"));
                    return Task.CompletedTask;
                }
            }
        }
        """;

    private const string Body = """
        public sealed class Stories : Raun.Scenarios<Raun.NoWorld>
        {
            public Dsl.Steps Given => Steps<Dsl.Steps>();
            [Raun.Scenario] public async System.Threading.Tasks.Task Story()
            {
                var three = await Given.Three();
                await Given.Record(three.Doubled());
            }
        }
        """;

    private static string Extension(string name = "Doubled", int factor = 2)
        => $"public static class Numbers{factor} {{ public static int {name}(this int value) => value * {factor}; }}";

    public static TheoryData<string, string> Layouts => new()
    {
        {
            "an extension method in the scenario's own namespace",
            $$"""
            namespace App.Tests
            {
                {{Extension()}}
                {{Body}}
            }
            """
        },
        {
            "an extension method in a parent of the scenario's namespace",
            $$"""
            namespace App { {{Extension()}} }
            namespace App.Tests.Clinic
            {
                {{Body}}
            }
            """
        },
        {
            "a using directive inside the scenario's namespace block",
            $$"""
            namespace Helpers { {{Extension()}} }
            namespace App.Tests
            {
                using Helpers;
                {{Body}}
            }
            """
        },
        {
            "a using directive after a file-scoped namespace",
            $$"""
            namespace App.Tests;
            using Helpers.Deep;
            {{Body}}
            """
        },
        {
            "a using directive that is relative to the namespace it stands in",
            $$"""
            namespace App.Helpers { {{Extension()}} }
            namespace App
            {
                namespace Tests
                {
                    using Helpers;
                    {{Body}}
                }
            }
            """
        },
        {
            "a using alias inside the scenario's namespace block",
            $$"""
            namespace Helpers { {{Extension()}} }
            namespace App.Tests
            {
                using H = Helpers.Numbers2;
                public sealed class Stories : Raun.Scenarios<Raun.NoWorld>
                {
                    public Dsl.Steps Given => Steps<Dsl.Steps>();
                    [Raun.Scenario] public async System.Threading.Tasks.Task Story()
                    {
                        var three = await Given.Three();
                        await Given.Record(H.Doubled(three));
                    }
                }
            }
            """
        },
        {
            "a using static inside the scenario's namespace block",
            $$"""
            namespace Helpers { {{Extension()}} }
            namespace App.Tests
            {
                using static Helpers.Numbers2;
                {{Body}}
            }
            """
        },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task What_was_in_scope_in_the_scenario_file_is_in_scope_in_the_generated_file(string what, string scenarioFile)
    {
        // The scenario's file on its own (a file-scoped namespace allows no other namespace beside
        // it); the steps and the helper the file-scoped layout imports are other files of the project.
        var result = GeneratorHarness.RunFiles(
            [scenarioFile, Dsl, "namespace Helpers.Deep { " + Extension() + " }"]);

        Assert.True(result.GeneratorDiagnostics.IsEmpty, what + ": " + string.Join("; ", result.GeneratorDiagnostics));
        Assert.True(result.EmitDiagnostics.IsEmpty, what + ": " + string.Join("; ", result.EmitDiagnostics));

        var results = await result.Definitions().Single().RunAsync();

        Assert.DoesNotContain(results, step => step.Status == StepStatus.Failed);
        var seen = (List<string>)result.Assembly!.GetType("Dsl.Probe")!.GetField("Seen")!.GetValue(null)!;
        Assert.Equal(["6"], seen);
    }
}
