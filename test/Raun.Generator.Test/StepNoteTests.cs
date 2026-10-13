using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public sealed class StepNoteTests
{
    private const string Source = """
        using System;
        using System.Threading.Tasks;
        using Raun;
        namespace Notes;
        public sealed class Steps : Given<NoWorld>
        {
            public Task<int> Value() => Task.FromResult(42);
            public Task<DayOfWeek> Day() => Task.FromResult(DayOfWeek.Monday);
            public Task Observe(int value) => Task.CompletedTask;
            public Task Fail() => throw new InvalidOperationException("expected");
            public Task<int> BrokenValue() => Task.FromException<int>(new InvalidOperationException("expected"));
        }
        public sealed class Scenarios : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task EnumNote()
            {
                var day = await Given.Day();
                await Given.Observe(42).Note($"The selected day is {day}");
            }
            [Scenario] public async Task Story()
            {
                var value = await Given.Value().Note("Create a known seed");
                await Given.Observe(value).Note($"Use seed {value}").Note((FormattableString)$"Seed {value:000}");
            }
            [Scenario] public async Task Skipped()
            {
                var value = await Given.Value();
                await Given.Fail().Note("An intentional failure");
                await Given.Observe(value).Note($"The available seed is {value}");
            }
            [Scenario] public async Task Unavailable()
            {
                var value = await Given.BrokenValue();
                await Given.Observe(value).Note($"Unavailable seed {value}").Note("This explanation remains");
            }
            [Scenario] public async Task Parallel()
            {
                await (Given.Value().Note("left"), Given.Value().Note("right"));
            }
        }
        """;

    [Fact]
    public async Task Interpolated_notes_can_read_enum_step_outputs()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var results = await generated.Definitions().Single(d => d.MethodName.EndsWith(".EnumNote", StringComparison.Ordinal)).RunAsync();
        Assert.Equal(["The selected day is Monday"], results.Single(r => r.Node.OperationName == "Observe").Notes);
    }

    [Fact]
    public async Task Notes_keep_the_original_nodes_and_resolve_step_outputs()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definition = generated.Definitions().Single(d => d.MethodName.EndsWith(".Story", StringComparison.Ordinal));
        Assert.Equal(4, definition.Nodes.Count);
        var results = await definition.RunAsync();
        Assert.Equal(["Create a known seed"], results.Single(r => r.Node.OperationName == "Value").Notes);
        Assert.Equal(["Use seed 42", "Seed 042"], results.Single(r => r.Node.OperationName == "Observe").Notes);
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
    }

    [Fact]
    public async Task Failure_and_skips_keep_notes_whose_inputs_are_available()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var results = await generated.Definitions().Single(d => d.MethodName.EndsWith(".Skipped", StringComparison.Ordinal)).RunAsync();
        var failed = results.Single(r => r.Node.OperationName == "Fail");
        Assert.Equal(StepStatus.Failed, failed.Status);
        Assert.Equal(["An intentional failure"], failed.Notes);
        var skipped = results.Single(r => r.Node.OperationName == "Observe");
        Assert.Equal(StepStatus.Skipped, skipped.Status);
        Assert.Equal(["The available seed is 42"], skipped.Notes);
    }

    [Fact]
    public async Task An_unavailable_note_does_not_hide_other_notes_or_change_the_outcome()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var results = await generated.Definitions().Single(d => d.MethodName.EndsWith(".Unavailable", StringComparison.Ordinal)).RunAsync();
        var skipped = results.Single(r => r.Node.OperationName == "Observe");
        Assert.Equal(StepStatus.Skipped, skipped.Status);
        Assert.Equal(["This explanation remains"], skipped.Notes);
    }

    [Fact]
    public async Task Parallel_steps_keep_their_own_notes()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definition = generated.Definitions().Single(d => d.MethodName.EndsWith(".Parallel", StringComparison.Ordinal));
        Assert.Equal(4, definition.Nodes.Count);
        var results = await definition.RunAsync();
        var values = results.Where(r => r.Node.OperationName == "Value").ToArray();
        Assert.Equal(["left"], values[0].Notes);
        Assert.Equal(["right"], values[1].Notes);
    }
}

