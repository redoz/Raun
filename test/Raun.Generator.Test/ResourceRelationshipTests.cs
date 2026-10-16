using Microsoft.CodeAnalysis;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class ResourceRelationshipTests
{
    internal const string Source = """
        using System.Threading.Tasks;
        using Raun;
        public sealed record Patient(string Id) : IResource<Patient> { public static ResourceKey KeyFor(Patient p) => p.Id; }
        public sealed record Slot(string Id) : IResource<Slot> { public static ResourceKey KeyFor(Slot p) => p.Id; }
        public sealed record Appointment(string Id) : IResource<Appointment> { public static ResourceKey KeyFor(Appointment p) => p.Id; }
        [ResourceActionDefinition(ResourceActionKind.Create)]
        public sealed class BookedAttribute : ResourceAttribute {
          [Relationship<Patient>(ResourceRelation.Reference)] public string Patient { get; set; } = "not a binding";
          [Relationship<Slot>(ResourceRelation.Consume)] public string Slot { get; set; } = "not a binding";
        }
        public sealed class Steps : Given<NoWorld> {
          [return: Created] public Task<Patient> Patient() => Task.FromResult(new Patient("p"));
          [return: Created] public Task<Slot> Slot() => Task.FromResult(new Slot("s"));
          [return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]
          public Task<Appointment> Book(Patient patient, Slot slot2) => Task.FromResult(new Appointment("a"));
        }
        public sealed class Stories : Scenarios<NoWorld> {
          public Steps Given => Steps<Steps>();
          [Scenario] public async Task Story() {
            var patient = await Given.Patient(); var slot = await Given.Slot();
            await Given.Book(patient, slot);
          }
        }
        """;

    [Fact]
    public async Task Domain_properties_bind_arbitrary_parameter_names_and_emit_both_relationships()
    {
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(Source, requireCompilable: true));
        var generated = GeneratorHarness.Run(Source); generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        var book = results.Single(r => r.Node.OperationName == "Book");
        Assert.Equal(2, book.Lineage.Count);
        Assert.Equal([LifecycleVerb.Reference, LifecycleVerb.Consume, LifecycleVerb.Create], book.Effects.Select(e => e.Verb));
        Assert.Equal("Booked", Assert.Single(book.Effects, e => e.Action is not null).Action!.Name);
        Assert.Contains(book.Lineage, r => r.Target.Key.Value == "p" && r.Kind == LifecycleVerb.Reference);
        Assert.Contains(book.Lineage, r => r.Target.Key.Value == "s" && r.Kind == LifecycleVerb.Consume);
    }

    [Theory]
    [InlineData("Booked(Patient = nameof(patient), Slot = nameof(slot2))", "Booked(Slot = nameof(slot2))", "RAUN032")]
    [InlineData("Patient = nameof(patient)", "Patient = \" \"", "RAUN032")]
    [InlineData("Slot = nameof(slot2)", "Slot = \"missing\"", "RAUN010")]
    [InlineData("public string Patient { get; set; }", "public int Patient { get; set; }", "RAUN031")]
    public async Task Bad_property_schemas_and_bindings_are_compile_errors(string oldValue, string newValue, string expected)
    {
        var source = Source.Replace(oldValue, newValue, StringComparison.Ordinal);
        if (expected == "RAUN031") { source = source.Replace("Patient = nameof(patient), ", "", StringComparison.Ordinal).Replace("= \"not a binding\"", "= 0", StringComparison.Ordinal); }
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: expected != "RAUN031");
        Assert.Contains(diagnostics, d => d.Id == expected);
        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN000");
    }

    [Fact]
    public async Task Standalone_relationships_are_the_same_contract()
    {
        var source = Source.Replace("[return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]",
            "[return: Created] [return: RelatesTo(ResourceRelation.Reference, nameof(patient))] [return: RelatesTo(ResourceRelation.Consume, nameof(slot2))]", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(2, book.Lineage.Count);
    }

    [Fact]
    public async Task Mixed_duplicate_edges_are_rejected()
    {
        var source = Source.Replace("public Task<Appointment> Book", "[return: RelatesTo(ResourceRelation.Reference, nameof(patient))] public Task<Appointment> Book", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == "RAUN032");
    }

    [Theory]
    [InlineData("Required = false", "")]
    [InlineData("Required = false", "Patient = null, ")]
    public async Task Optional_properties_allow_omission_but_reject_explicit_null(string options, string binding)
    {
        var source = Source.Replace("Relationship<Patient>(ResourceRelation.Reference)", "Relationship<Patient>(ResourceRelation.Reference, " + options + ")", StringComparison.Ordinal)
            .Replace("Patient = nameof(patient), ", binding, StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        if (binding.Length == 0)
        {
            // The unreferenced patient still needs its own Read declaration.
            diagnostics = await GeneratorHarness.DiagnoseAsync(source.Replace("Book(Patient patient", "Book([Read] Patient patient", StringComparison.Ordinal), requireCompilable: true);
            Assert.DoesNotContain(diagnostics, d => d.Id is "RAUN032" or "RAUN000");
        }
        else { Assert.Contains(diagnostics, d => d.Id == "RAUN032"); }
    }

    [Fact]
    public async Task Null_array_bindings_are_diagnosed_without_an_analyzer_crash()
    {
        var source = Source.Replace("[return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]", "[return: Created(References = null)]", StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Contains(diagnostics, d => d.Id == "RAUN032");
        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN000");
    }

    [Theory]
    [InlineData("Row")]
    [InlineData("Response")]
    public async Task Typed_relationships_accept_canonical_alternates_in_generated_code(string alternate)
    {
        var source = Source.Replace("public sealed record Patient(string Id) : IResource<Patient>",
            "public sealed record Row(string Id); public sealed record Response(string Id); public sealed record Patient(string Id) : IResource<Patient>, IResourceAlternate<Row>, IResourceAlternate<Response>", StringComparison.Ordinal)
            .Replace("public static ResourceKey KeyFor(Patient p) => p.Id;", "public static ResourceKey KeyFor(Patient p) => p.Id; public static ResourceKey KeyFor(Row p) => p.Id; public static ResourceKey KeyFor(Response p) => p.Id;", StringComparison.Ordinal)
            .Replace("public Task<Patient> Patient() => Task.FromResult(new Patient", "public Task<" + alternate + "> Patient() => Task.FromResult(new " + alternate, StringComparison.Ordinal)
            .Replace("Book(Patient patient", "Book(" + alternate + " patient", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal("Patient", Assert.Single(results.Single(r => r.Node.OperationName == "Book").Lineage, r => r.Kind == LifecycleVerb.Reference).Target.Type!.Name);
    }

    [Fact]
    public async Task Explicit_kind_mismatches_fail_the_step_without_recording_the_relation()
    {
        var source = Source.Replace("Relationship<Patient>(ResourceRelation.Reference)", "Relationship<Slot>(ResourceRelation.Reference)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(StepStatus.Failed, book.Status);
        Assert.IsType<ArgumentException>(book.Exception);
        Assert.Empty(book.Lineage);
    }

    [Fact]
    public async Task Referenced_library_properties_and_usage_errors_are_read_from_metadata()
    {
        var declarations = Source[..Source.IndexOf("public sealed class Stories", StringComparison.Ordinal)];
        var library = GeneratorHarness.Run(declarations, "Bookings"); library.AssertCompiles();
        var story = Source[Source.IndexOf("public sealed class Stories", StringComparison.Ordinal)..];
        var application = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        application.AssertCompiles();
        Assert.All(await Assert.Single(application.Definitions()).RunAsync(), r => Assert.Equal(StepStatus.Passed, r.Status));
        var badLibrary = GeneratorHarness.Run(declarations.Replace("Patient = nameof(patient), ", "", StringComparison.Ordinal), "BadBookings"); badLibrary.AssertCompiles();
        var bad = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + story, "BadConsumer", MetadataReference.CreateFromImage(badLibrary.Image));
        Assert.Single(bad.GeneratorDiagnostics, d => d.Id == "RAUN032");
    }

    [Fact]
    public async Task Private_generic_property_contracts_emit_accessible_metadata_lookups()
    {
        var source = Source.Replace("public sealed class Steps : Given<NoWorld> {", """
            public sealed class Steps : Given<NoWorld> {
              [ResourceActionDefinition(ResourceActionKind.Create)]
              private sealed class LocalAttribute<T> : ResourceAttribute {
                [Relationship<Patient>(ResourceRelation.Reference)] public string Patient { get; set; } = "";
                [Relationship<Slot>(ResourceRelation.Consume)] public string Slot { get; set; } = "";
              }
            """, StringComparison.Ordinal).Replace("[return: Booked(", "[return: Local<string>(", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal(2, results.Single(r => r.Node.OperationName == "Book").Lineage.Count);
    }

    [Theory]
    [InlineData("[ResourceActionDefinition(ResourceActionKind.Create)]", "", "RAUN030")]
    [InlineData("ResourceActionKind.Create", "(ResourceActionKind)77", "RAUN030")]
    [InlineData("ResourceRelation.Reference", "(ResourceRelation)77", "RAUN031")]
    public async Task Invalid_unused_declarations_are_diagnosed(string before, string after, string id)
    {
        var source = Source[..Source.IndexOf("public sealed class Steps", StringComparison.Ordinal)].Replace(before, after, StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == id);
    }

    [Fact]
    public async Task A_field_cannot_impersonate_an_inherited_relationship_property()
    {
        var source = Source.Replace("public sealed class Steps", "public sealed class HiddenAttribute : CreatedAttribute { public new string[] References = []; } public sealed class Steps", StringComparison.Ordinal)
            .Replace("[return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]", "[return: Hidden(References = [nameof(patient), nameof(slot2)])]", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == "RAUN031");
    }

    [Theory]
    [InlineData("private new string[] References { get; set; } = [];")]
    [InlineData("[Relationship(ResourceRelation.Reference)] private string Secret { get; set; } = \"\";")]
    public void Referenced_private_members_receive_the_same_schema_validation(string member)
    {
        var declarations = Source[..Source.IndexOf("public sealed class Stories", StringComparison.Ordinal)]
            .Replace("public sealed class Steps", "public sealed class HiddenAttribute : CreatedAttribute { " + member + " } public sealed class Steps", StringComparison.Ordinal)
            .Replace("[return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]", "[return: Hidden]", StringComparison.Ordinal)
            .Replace("Book(Patient patient, Slot slot2)", "Book([Read] Patient patient, [Read] Slot slot2)", StringComparison.Ordinal);
        var library = GeneratorHarness.Run(declarations, "PrivateBookings"); library.AssertCompiles();
        var story = Source[Source.IndexOf("public sealed class Stories", StringComparison.Ordinal)..];
        var consumer = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        Assert.Single(consumer.GeneratorDiagnostics, d => d.Id == "RAUN031");
    }
}
