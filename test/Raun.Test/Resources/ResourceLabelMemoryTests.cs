using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

public class ResourceLabelMemoryTests
{
    internal sealed record Patient(string Id, string? Name) : IResource<Patient>
    {
        public static ResourceKey KeyFor(Patient value) => value.Id;
        public static string? LabelFor(Patient value) => value.Name;
    }
    internal static ScenarioNode Node(int i, int[]? dependencies = null) => new()
    {
        Index = i, StepId = $"s{i}", Phase = "Given", OperationName = "Test",
        DisplayNameTemplate = "test", DependsOn = dependencies ?? [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };
    private static readonly ResourceIdentity Key = new(typeof(Patient), "42");

    [Fact]
    public void Key_only_formats_reuse_names_without_copying_data_and_freeze_earlier_evidence()
    {
        var formatting = new MessageFormattingContext(new ResourceIdentityResolver());
        var before = StepMessage.Create(formatting, $"{new Patient("42", "Jane")}");
        var key = StepMessage.Create(formatting, $"{Key} / {Key:id} / {Key:data}");
        Assert.Equal("Jane / 42 / Jane", key.Text);
        Assert.All(key.Parts.Where(p => p.Resource.HasValue), p => Assert.Null(p.Snapshot));
        StepMessage.Create(formatting, $"{new Patient("42", "New name")}");
        Assert.Equal("Jane", before.Text);
        Assert.Equal("Jane / 42 / Jane", key.Text);
        Assert.Equal("New name", StepMessage.Create(formatting, $"{Key}").Text);
        StepMessage.Create(formatting, $"{new Patient("42", " ")}");
        Assert.Equal("New name", StepMessage.Create(formatting, $"{Key}").Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Independent_branches_have_stable_tie_breaks_and_no_sideways_visibility(bool reverse)
    {
        var resolver = new ResourceIdentityResolver();
        var memory = new ResourceLabelMemory([Node(0), Node(1), Node(2, [0, 1])]);
        var a = memory.ForStep(0); var b = memory.ForStep(1); var join = memory.ForStep(2);
        void Observe(ResourceLabelScope scope, string name) => scope.Resolve(resolver, Key, typeof(Patient), new Patient("42", name));
        if (reverse) { Observe(b, "B"); Observe(a, "A"); }
        else { Observe(a, "A"); Observe(b, "B"); }
        Assert.Equal("A", a.Resolve(resolver, Key, null, null));
        Assert.Equal("B", b.Resolve(resolver, Key, null, null));
        Assert.Equal("B", join.Resolve(resolver, Key, null, null));
        Assert.Equal("Patient:42", memory.ForStep(-1).Resolve(resolver, Key, null, null));
    }

    [Fact]
    public void Causal_recency_wins_even_when_indices_are_not_topological()
    {
        var resolver = new ResourceIdentityResolver();
        var memory = new ResourceLabelMemory([Node(0, [2]), Node(1, [0, 2]), Node(2)]);
        memory.ForStep(2).Resolve(resolver, Key, typeof(Patient), new Patient("42", "Old"));
        memory.ForStep(0).Resolve(resolver, Key, typeof(Patient), new Patient("42", "New"));
        Assert.Equal("New", memory.ForStep(1).Resolve(resolver, Key, null, null));
    }

    [Fact]
    public void Canonical_representations_share_names_but_kinds_and_executions_do_not()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Row>(r => r.Id);
        resolver.Label<Row>(r => r.Name);
        var formatting = new MessageFormattingContext(resolver);
        StepMessage.Create(formatting, $"{new Row("42", "Jane")}");
        Assert.Equal("Jane", StepMessage.Create(formatting, $"{Key}").Text);
        Assert.Equal("Other:42", StepMessage.Create(formatting, $"{new ResourceIdentity("Other", "42")}").Text);
        Assert.Equal("Patient:42", StepMessage.Create(new MessageFormattingContext(resolver), $"{Key}").Text);
    }
    private sealed record Row(string Id, string Name);

    [Fact]
    public void Actual_label_equal_to_fallback_is_still_an_observation()
    {
        var resolver = new ResourceIdentityResolver();
        var memory = new ResourceLabelMemory([Node(0), Node(1, [0])]);
        memory.ForStep(0).Resolve(resolver, Key, typeof(Patient), new Patient("42", "Earlier"));
        memory.ForStep(1).Resolve(resolver, Key, typeof(Patient), new Patient("42", "Patient:42"));
        Assert.Equal("Patient:42", memory.ForStep(1).Resolve(resolver, Key, null, null));
    }
}
