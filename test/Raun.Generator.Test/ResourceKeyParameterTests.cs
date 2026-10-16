using Microsoft.CodeAnalysis;
using Raun;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class ResourceKeyParameterTests
{
    [Theory]
    [InlineData("Read", "string", LifecycleVerb.Read)]
    [InlineData("Edited", "string", LifecycleVerb.Edit)]
    [InlineData("Deleted", "string", LifecycleVerb.Delete)]
    [InlineData("Read", "ResourceKey", LifecycleVerb.Read)]
    public async Task Key_parameters_trace_the_same_resource_as_the_full_object(string role, string keyType, LifecycleVerb expected)
    {
        var generated = GeneratorHarness.Run(Source(role, keyType));
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(Source(role, keyType)));
        var results = await generated.Definitions().Single().RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var created = Assert.Single(results[1].Effects);
        var touched = Assert.Single(results[2].Effects);
        Assert.Equal(created.Identity, touched.Identity);
        Assert.Equal(expected, touched.Verb);
        Assert.Null(touched.Data);
        Assert.Null(touched.DataSnapshot);
        Assert.Equal("Jane", touched.Label);
        Assert.Equal("touch Jane / 123 / Jane", results[2].DisplayName);
        var references = results[2].TitleMessage.Parts.Where(part => part.Resource.HasValue).ToArray();
        Assert.Equal(3, references.Length);
        Assert.All(references, part =>
        {
            Assert.Equal(created.Identity, part.Resource);
            Assert.Null(part.Snapshot);
        });
    }

    [Fact]
    public async Task Key_and_object_claims_conflict_even_when_the_key_is_a_constant()
    {
        var source = Source("Edited", "string").Replace(
            "await Given.Touch(patient.Id);", "await (Given.Touch(\"123\"), Given.ReadObject(patient));", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await generated.Definitions().Single().RunAsync();
        var failed = Assert.Single(results, result => result.Status == StepStatus.Failed);
        Assert.IsType<ResourceConflictException>(failed.Exception);
    }

    [Theory]
    [InlineData("References", LifecycleVerb.Reference)]
    [InlineData("Consumes", LifecycleVerb.Consume)]
    public async Task Key_parameters_named_by_lineage_use_the_canonical_resource(string relation, LifecycleVerb expected)
    {
        var source = Source("Read", "string")
            .Replace("[Read, ResourceKey<Patient>]", "[ResourceKey<Patient>]", StringComparison.Ordinal)
            .Replace("public Task Touch(", "[return: Created(" + relation + " = [nameof(patientId)])]\n    public Task<Booking> Touch(", StringComparison.Ordinal)
            .Replace("return Task.CompletedTask;", "return Task.FromResult(new Booking(\"booking\"));", StringComparison.Ordinal)
            + "\npublic sealed record Booking(string Id) : IResource<Booking> { public static ResourceKey KeyFor(Booking booking) => booking.Id; }";
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await generated.Definitions().Single().RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var patient = Assert.Single(results[1].Effects).Identity;
        var lineage = Assert.Single(results[2].Lineage);
        Assert.Equal(patient, lineage.Target);
        Assert.Equal("Booking:booking", lineage.Subject.ToString());
        Assert.Equal(expected, lineage.Kind);
        Assert.Equal(new[] { expected, LifecycleVerb.Create }, results[2].Effects.Select(effect => effect.Verb));
    }

    [Fact]
    public async Task Key_parameters_require_an_access_role_even_without_a_title()
    {
        var source = Source("Read", "string")
            .Replace("[Read, ResourceKey<Patient>]", "[ResourceKey<Patient>]", StringComparison.Ordinal)
            .Replace("[StepName(\"touch {patientId} / {patientId:id} / {patientId:data}\")]", "", StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Equal("RAUN009", Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("int")]
    [InlineData("object")]
    public async Task Resource_key_annotation_rejects_other_parameter_types(string type)
    {
        var source = Source("Read", type).Replace("await Given.Touch(patient.Id);", "await Given.Touch(default!);", StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("RAUN024", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task Nullable_string_keys_compile_without_generated_warnings_when_supplied()
    {
        var source = Source("Read", "string?").Replace("patientId.ToString()", "patientId?.ToString()", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var results = await generated.Definitions().Single().RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        Assert.Equal(Assert.Single(results[1].Effects).Identity, Assert.Single(results[2].Effects).Identity);
    }

    [Theory]
    [InlineData("string?", "null")]
    [InlineData("ResourceKey", "default(ResourceKey)")]
    public async Task Null_keys_fail_before_recording_a_resource(string keyType, string argument)
    {
        var source = Source("Read", keyType)
            .Replace("patientId.ToString()", keyType == "string?" ? "patientId?.ToString()" : "patientId.ToString()", StringComparison.Ordinal)
            .Replace("Given.Touch(patient.Id)", "Given.Touch(" + argument + ")", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var results = await generated.Definitions().Single().RunAsync();
        Assert.Equal(StepStatus.Failed, results[2].Status);
        Assert.Equal("patientId", Assert.IsType<ArgumentNullException>(results[2].Exception).ParamName);
        Assert.Empty(results[2].Effects);
    }

    private static string Source(string role, string keyType) => $$"""
        using System;
        using System.Threading.Tasks;
        using Raun;
        public sealed record Patient(string Id, string Name) : IResource<Patient>
        {
            public static ResourceKey KeyFor(Patient patient) => patient.Id;
            public static string LabelFor(Patient patient) => patient.Name;
        }
        public sealed class Steps : Given<NoWorld>
        {
            [StepName("a patient")]
            [return: Created]
            public Task<Patient> Seed() => Task.FromResult(new Patient("123", "Jane"));
            [StepName("touch {patientId} / {patientId:id} / {patientId:data}")]
            public Task Touch([{{role}}, ResourceKey<Patient>] {{keyType}} patientId)
            {
                if (patientId.ToString() != "123") throw new InvalidOperationException("argument changed");
                return Task.CompletedTask;
            }
            public Task ReadObject([Read] Patient patient) => Task.CompletedTask;
        }
        public sealed class Stories : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task Story()
            {
                var patient = await Given.Seed();
                await Given.Touch(patient.Id);
            }
        }
        """;
}