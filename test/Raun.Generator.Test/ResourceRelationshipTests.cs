using Microsoft.CodeAnalysis;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class ResourceRelationshipTests
{
    internal const string Source = CrudResourceGeneratorTests.Source;

    [Fact]
    public async Task Domain_roles_bind_arbitrary_parameter_names_and_emit_both_links()
    {
        var source = Source.Replace("Slot slot)", "Slot slot2)", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(StepStatus.Passed, book.Status);
        Assert.Equal([ResourceEffect.Read, ResourceEffect.Update, ResourceEffect.Create], book.Effects.Select(e => e.Effect));
        Assert.Contains(book.Links, r => r.Target.Key.Value == "p" && r.Role == "Patient");
        Assert.Contains(book.Links, r => r.Target.Key.Value == "s" && r.Role == "Slot");
    }

    [Fact]
    public async Task Named_link_targets_keep_one_action_and_their_snapshot()
    {
        var generated = GeneratorHarness.Run(Source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        var reserved = Assert.Single(book.Effects, e => e.Identity.Key.Value == "s");
        Assert.Equal(ResourceEffect.Update, reserved.Effect);
        Assert.Equal(LockMode.Exclusive, reserved.Effect.ToLockMode());
        Assert.Equal("Reserve", reserved.Action!.Name);
        Assert.Single(book.Logs, line => line.Contains("[resource] Reserve", StringComparison.Ordinal));
        Assert.NotNull(reserved.DataSnapshot);
        Assert.Contains(book.Links, link => link.Target == reserved.Identity && link.Role == "Slot");
    }

    [Theory]
    [InlineData("[Booked.Patient]", "[Read]", "RAUN036")]
    [InlineData("[Booked.Patient]", "[Read, Link<Booked>(\"Unknown\")]", "RAUN032")]
    [InlineData("[Booked.Patient]", "[Read, Link<Booked>(\"Patient\", Source = \"unknown\")]", "RAUN032")]
    [InlineData("public sealed class PatientAttribute", "private sealed class PatientAttribute", "RAUN031")]
    public async Task Bad_role_schemas_and_bindings_are_compile_errors(string before, string after, string expected)
    {
        var diagnostics = await GeneratorHarness.DiagnoseAsync(Source.Replace(before, after, StringComparison.Ordinal), requireCompilable: expected != "RAUN031");
        Assert.Contains(diagnostics, d => d.Id == expected);
        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN000");
    }

    [Fact]
    public async Task Raw_links_require_their_own_input_action()
    {
        var source = Source.Replace("[Booked.Patient]", "[Link<Booked>(\"Patient\")]", StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Contains(diagnostics, d => d.Id == "RAUN009");
    }

    [Fact]
    public async Task Mixed_duplicate_role_bindings_are_rejected()
    {
        var source = Source.Replace("[Booked.Patient]", "[Booked.Patient, Link<Booked>(\"Patient\")]", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == "RAUN032");
    }

    [Fact]
    public async Task Packaged_marker_and_explicit_action_compete()
    {
        var source = Source.Replace("[Booked.Patient]", "[Booked.Patient, Read]", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == "RAUN028");
    }

    [Theory]
    [InlineData("Row")]
    [InlineData("Response")]
    public async Task Roles_accept_canonical_alternates(string alternate)
    {
        var source = Source.Replace("public record Patient(string Id) : IResource<Patient>",
            "public record Row(string Id); public record Response(string Id); public record Patient(string Id) : IResource<Patient>, IResourceAlternate<Row>, IResourceAlternate<Response>", StringComparison.Ordinal)
            .Replace("public static ResourceKey KeyFor(Patient value) => value.Id;", "public static ResourceKey KeyFor(Patient value) => value.Id; public static ResourceKey KeyFor(Row value) => value.Id; public static ResourceKey KeyFor(Response value) => value.Id;", StringComparison.Ordinal)
            .Replace("Task<Patient> Patient() => Task.FromResult(new Patient", "Task<" + alternate + "> Patient() => Task.FromResult(new " + alternate, StringComparison.Ordinal)
            .Replace("[Booked.Patient] Patient patient", "[Booked.Patient] " + alternate + " patient", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal("Patient", Assert.Single(results.Single(r => r.Node.OperationName == "Book").Links, r => r.Role == "Patient").Target.Type!.Name);
    }

    [Fact]
    public async Task Canonical_kind_mismatches_are_compile_errors()
    {
        var source = Source.Replace("ResourceLinkAttribute<Patient, ReadAttribute>", "ResourceLinkAttribute<Slot, ReadAttribute>", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == "RAUN032");
    }

    [Fact]
    public async Task Referenced_library_roles_and_usage_errors_are_read_from_metadata()
    {
        var start = Source.IndexOf("public sealed class Stories", StringComparison.Ordinal);
        var declarations = Source[..start]; var story = Source[start..];
        var library = GeneratorHarness.Run(declarations, "Bookings"); library.AssertCompiles();
        var application = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        application.AssertCompiles();
        Assert.All(await Assert.Single(application.Definitions()).RunAsync(), r => Assert.Equal(StepStatus.Passed, r.Status));
        var badLibrary = GeneratorHarness.Run(declarations.Replace("[Booked.Patient]", "[Read]", StringComparison.Ordinal), "BadBookings"); badLibrary.AssertCompiles();
        var bad = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + story, "BadConsumer", MetadataReference.CreateFromImage(badLibrary.Image));
        Assert.Single(bad.GeneratorDiagnostics, d => d.Id == "RAUN036");
    }

    [Fact]
    public async Task Private_generic_owners_emit_accessible_metadata_lookups()
    {
        var source = Source.Replace("public sealed class Operations : Given<NoWorld> {", """
            public sealed class Operations : Given<NoWorld> {
              private sealed class Local<T> : CreatedAttribute {
                public sealed class PatientAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
                public sealed class SlotAttribute : ResourceLinkAttribute<Slot, Reserved>;
              }
            """, StringComparison.Ordinal).Replace("[return: Booked]", "[return: Local<string>]", StringComparison.Ordinal)
            .Replace("[Booked.Patient]", "[Local<string>.Patient]", StringComparison.Ordinal).Replace("[Booked.Slot]", "[Local<string>.Slot]", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal(2, results.Single(r => r.Node.OperationName == "Book").Links.Count);
    }

    [Theory]
    [InlineData("public sealed class Booked : CreatedAttribute", "public sealed class Booked : ResourceAttribute", "RAUN030")]
    [InlineData("public sealed class Booked : CreatedAttribute", "[ResourceActionDefinition((ResourceEffect)77)] public sealed class Booked : ResourceAttribute", "RAUN030")]
    [InlineData("public sealed class PatientAttribute", "private sealed class PatientAttribute", "RAUN031")]
    public async Task Invalid_unused_declarations_are_diagnosed(string before, string after, string id)
    {
        var source = Source[..Source.IndexOf("public sealed class Operations", StringComparison.Ordinal)].Replace(before, after, StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), d => d.Id == id);
    }

    [Theory]
    [InlineData("public new string Patient = \"\";")]
    [InlineData("private new string Patient { get; set; } = \"\";")]
    public void Referenced_members_cannot_hide_inherited_roles(string member)
    {
        var start = Source.IndexOf("public sealed class Stories", StringComparison.Ordinal);
        var declarations = Source[..start].Replace("public sealed class Booked", "public class Booked", StringComparison.Ordinal)
            .Replace("public sealed class Operations", "public sealed class Hidden : Booked { " + member + " } public sealed class Operations", StringComparison.Ordinal)
            .Replace("[return: Booked]", "[return: Hidden]", StringComparison.Ordinal);
        var library = GeneratorHarness.Run(declarations, "HiddenBookings"); library.AssertCompiles();
        var consumer = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + Source[start..], "Consumer", MetadataReference.CreateFromImage(library.Image));
        Assert.Contains(consumer.GeneratorDiagnostics, d => d.Id == "RAUN031");
    }
    [Fact]
    public async Task Raw_links_cannot_borrow_roles_from_an_applied_derived_action()
    {
        var source = Source.Replace("[Booked.Patient]", "[Read, Link<CreatedAttribute>(\"Patient\")]", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), diagnostic => diagnostic.Id == "RAUN032");
    }

    [Fact]
    public async Task Raw_links_can_use_inherited_roles_on_a_derived_source_action()
    {
        var source = Source.Replace("public sealed class Booked", "public class Booked", StringComparison.Ordinal)
            .Replace("public sealed class Operations", "public sealed class SpecialBooked : Booked; public sealed class Operations", StringComparison.Ordinal)
            .Replace("[return: Booked]", "[return: SpecialBooked]", StringComparison.Ordinal)
            .Replace("[Booked.Patient]", "[Read, Link<Booked>(\"Patient\")]", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(result => result.Node.OperationName == "Book");
        Assert.Equal(StepStatus.Passed, book.Status);
        Assert.Equal(["Patient", "Slot"], book.Links.Select(link => link.Role));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Role_member_collisions_are_invalid_in_any_order_and_in_referenced_metadata(bool memberFirst)
    {
        const string member = "public string Patient { get; set; } = \"\";";
        var source = memberFirst
            ? Source.Replace("class Booked : CreatedAttribute {", "class Booked : CreatedAttribute { " + member, StringComparison.Ordinal)
            : Source.Replace("public sealed class SlotAttribute", member + " public sealed class SlotAttribute", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), diagnostic => diagnostic.Id == "RAUN031");
        var start = source.IndexOf("public sealed class Stories", StringComparison.Ordinal);
        var library = GeneratorHarness.Run(source[..start], "CollidingRoles"); library.AssertCompiles();
        Assert.Throws<ArgumentException>(() => ResourceAction.For(library.Assembly!.GetType("Booked")!));
        var consumer = GeneratorHarness.Run("using Raun; using System.Threading.Tasks; " + source[start..], "Consumer", MetadataReference.CreateFromImage(library.Image));
        Assert.Contains(consumer.GeneratorDiagnostics, diagnostic => diagnostic.Id == "RAUN031");
    }

}
