using Microsoft.CodeAnalysis;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public sealed class CustomResourceActionTests
{
    [Theory]
    [InlineData("Created", "Create")]
    [InlineData("Loaded", "Load")]
    [InlineData("Read", "Read")]
    [InlineData("Edited", "Edit")]
    [InlineData("Deleted", "Delete")]
    public async Task Custom_actions_work_on_inputs_and_returns(string basis, string effect)
    {
        var generated = GeneratorHarness.Run(Source(basis));
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var input = Assert.Single(results[2].Effects);
        Assert.Equal(effect, input.Verb.ToString());
        Assert.Equal("Domain action", input.Action!.Name);
        Assert.Equal(2, results[3].Effects.Count);
        Assert.Equal("Domain action", Assert.Single(results[3].Effects, effect => effect.Action is not null).Action!.Name);
        Assert.Equal(LifecycleVerb.Read, Assert.Single(results[3].Effects, effect => effect.Action is null).Verb);
        Assert.Contains("[resource] Domain action", results[2].Logs[0], StringComparison.Ordinal);
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(Source(basis)));
    }

    [Fact]
    public async Task Private_generic_attributes_keep_their_type_and_default_name()
    {
        var source = Source("Edited").Replace("[Custom]", "[Local<string>]", StringComparison.Ordinal)
            .Replace("public sealed class Steps : Given<NoWorld> {", "public sealed class Steps : Given<NoWorld> { private sealed class LocalAttribute<T> : EditedAttribute;", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        Assert.Equal("Local", Assert.Single(results[2].Effects).Action!.Name);
        Assert.True(Assert.Single(results[2].Effects).Action!.AttributeType!.IsConstructedGenericType);
    }

    [Theory]
    [InlineData("Created", false)]
    [InlineData("Created", true)]
    [InlineData("Custom", false)]
    [InlineData("Custom", true)]
    public async Task Create_input_conflicts_with_read_in_both_group_orders(string role, bool reverse)
    {
        var source = Source("Created").Replace("[Custom] Patient patient", "[" + role + "] Patient patient", StringComparison.Ordinal)
            .Replace("await Given.Touch(patient);", reverse ? "await (Given.Observe(patient), Given.Touch(patient));" : "await (Given.Touch(patient), Given.Observe(patient));", StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RAUN013");
    }

    [Fact]
    public async Task Loaded_and_read_inputs_remain_shared()
    {
        var source = Source("Loaded").Replace("await Given.Touch(patient);", "await (Given.Touch(patient), Given.Observe(patient));", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
    }

    [Theory]
    [InlineData("[Custom, Read]", "RAUN028")]
    [InlineData("[Custom]", "RAUN027")]
    public async Task Unadorned_local_methods_receive_one_metadata_diagnostic(string annotation, string expected)
    {
        var source = Source("Edited").Replace("[Custom] Patient patient", annotation + " Patient patient", StringComparison.Ordinal);
        if (expected == "RAUN027") { source = source.Replace("Domain action", " ", StringComparison.Ordinal).Replace("[return: Custom]", "[return: Read]", StringComparison.Ordinal); }
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Single(diagnostics, diagnostic => diagnostic.Id == expected);
    }

    [Fact]
    public async Task Method_role_without_a_return_value_is_diagnosed()
    {
        var source = Source("Edited").Replace("public Task Touch(", "[Custom] public Task Touch(", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), diagnostic => diagnostic.Id == "RAUN029");
    }

    [Theory]
    [InlineData("[Custom, Read]", "RAUN028")]
    [InlineData("[Custom]", "RAUN027")]
    public void Referenced_invalid_roles_are_reported_on_the_consumers_call(string annotation, string expected)
    {
        var library = GeneratorHarness.Run(Declarations.Replace("BASE", "Edited", StringComparison.Ordinal).Replace("Domain action", " ", StringComparison.Ordinal) + """
            public sealed class Steps : Given<NoWorld> {
            public Task Touch(ANNOTATION Patient patient) => Task.CompletedTask;
            public Task<Patient> Seed() => Task.FromResult(new Patient("42")); }
            """.Replace("ANNOTATION", annotation, StringComparison.Ordinal), "Library");
        library.AssertCompiles();
        var application = GeneratorHarness.Run(Story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        var diagnostic = Assert.Single(application.GeneratorDiagnostics, diagnostic => diagnostic.Id == expected);
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Theory]
    [InlineData("Read", false)]
    [InlineData("Loaded", false)]
    [InlineData("Loaded", true)]
    public async Task Explicit_return_role_replaces_the_complete_method_lineage(string role, bool alternate)
    {
        var source = Source("Edited").Replace("[return: Custom] public Task<Patient> Copy([Read] Patient patient)",
            "[Created(References = [nameof(patient)])] [return: " + role + (alternate ? "(Consumes = [nameof(patient)])" : "") + "] public Task<Patient> Copy([Read] Patient patient)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        if (alternate) { Assert.Equal(LifecycleVerb.Consume, Assert.Single(results[3].Lineage).Kind); }
        else { Assert.Empty(results[3].Lineage); }
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Loaded")]
    [InlineData("Read")]
    [InlineData("Edited")]
    [InlineData("Deleted")]
    public async Task Custom_key_only_inputs_do_not_record_wrapper_data(string basis)
    {
        var source = Source(basis).Replace("[Custom] Patient patient", "[Custom, ResourceKey<Patient>] string patient", StringComparison.Ordinal)
            .Replace("Given.Touch(patient)", "Given.Touch(patient.Id)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var effect = Assert.Single(results[2].Effects);
        Assert.Equal("Domain action", effect.Action!.Name);
        Assert.Null(effect.Data);
        Assert.Null(effect.DataSnapshot);
        Assert.Equal(Assert.Single(results[1].Effects).Identity, effect.Identity);
    }

    [Fact]
    public async Task Referenced_library_action_metadata_executes_without_constructing_the_attribute()
    {
        var library = GeneratorHarness.Run(Declarations.Replace("BASE", "Edited", StringComparison.Ordinal)
            .Replace("public sealed class CustomAttribute : EditedAttribute;", "public sealed class CustomAttribute : EditedAttribute { static CustomAttribute() => throw new System.Exception(\"initialized\"); public CustomAttribute() => throw new System.Exception(\"constructed\"); }", StringComparison.Ordinal) + """
            public sealed class Steps : Given<NoWorld> {
            [return: Created] public Task<Patient> Seed() => Task.FromResult(new Patient("42"));
            public Task Touch([Custom] Patient patient) => Task.CompletedTask; }
            """, "Library");
        library.AssertCompiles();
        var generated = GeneratorHarness.Run(Story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        Assert.Equal(library.Assembly, Assert.Single(results[2].Effects).Action!.AttributeType!.Assembly);
    }

    private static string Source(string basis) => Declarations.Replace("BASE", basis, StringComparison.Ordinal) + """
        public sealed class Steps : Given<NoWorld> {
        [return: Created] public Task<Patient> Seed() => Task.FromResult(new Patient("42"));
        public Task Touch([Custom] Patient patient) => Task.CompletedTask;
        public Task Observe([Read] Patient patient) => Task.CompletedTask;
        [return: Custom] public Task<Patient> Copy([Read] Patient patient) => Task.FromResult(new Patient("other")); }
        """ + Story.Replace("await Given.Touch(patient);", "await Given.Touch(patient); await Given.Copy(patient);", StringComparison.Ordinal);

    private const string Declarations = """
        using System.Threading.Tasks; using System.ComponentModel; using Raun;
        public record Patient(string Id) : IResource<Patient> { public static ResourceKey KeyFor(Patient value) => value.Id; }
        [DisplayName("Domain action")] public sealed class CustomAttribute : BASEAttribute;
        """;
    private const string Story = """
        public sealed class Stories : Raun.Scenarios<Raun.NoWorld> {
        public Steps Given => Steps<Steps>();
        [Raun.Scenario] public async System.Threading.Tasks.Task Story() {
        var patient = await Given.Seed(); await Given.Touch(patient); } }
        """;
}
