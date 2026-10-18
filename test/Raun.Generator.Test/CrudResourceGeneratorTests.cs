using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Raun.Generator.Test;

public class CrudResourceGeneratorTests
{
    [Fact]
    public void Editor_facade_exists_in_the_compiler_assembly()
        => Assert.NotNull(typeof(ScenarioGenerator).Assembly.GetType("Raun.Generator.ResourceCodeModel"));

    [Fact]
    public async Task Packaged_roles_record_exactly_their_actions_and_named_links()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(Raun.Model.StepStatus.Passed, result.Status));
        var booking = results.Single(result => result.Node.OperationName == "Book");
        Assert.Equal(["Read", "Reserve", "Booked"], booking.Effects.Select(effect => effect.Action?.Name ?? effect.Effect.ToString()));
        Assert.Equal([ResourceEffect.Read, ResourceEffect.Update, ResourceEffect.Create], booking.Effects.Select(effect => effect.Effect));
        Assert.Equal(["Patient", "Slot"], booking.Links.Select(link => link.Role));
    }

    [Fact]
    public async Task Explicit_roles_produce_the_same_actions_and_links()
    {
        var explicitSource = Source.Replace("[Booked.Patient]", "[Read, Link<Booked>(\"Patient\")]", StringComparison.Ordinal)
            .Replace("[Booked.Slot]", "[Reserved, Link<Booked>(\"Slot\")]", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(explicitSource);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        var booking = results.Single(result => result.Node.OperationName == "Book");
        Assert.Equal([ResourceEffect.Read, ResourceEffect.Update, ResourceEffect.Create], booking.Effects.Select(effect => effect.Effect));
        Assert.Equal(["Patient", "Slot"], booking.Links.Select(link => link.Role));
    }

    [Fact]
    public async Task Missing_role_has_editable_diagnostic_and_structured_owner_metadata()
    {
        var diagnostics = await GeneratorHarness.DiagnoseAsync(Source.Replace("[Booked.Slot]", "[Read]", StringComparison.Ordinal), requireCompilable: true);
        var missing = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "RAUN036");
        Assert.Equal("Slot", missing.Properties["Raun.RoleName"]);
        Assert.Equal("T:Booked", missing.Properties["Raun.OwnerId"]);
        Assert.Equal("$return", missing.Properties["Raun.Source"]);
    }

    [Fact]
    public async Task Failed_return_records_input_actions_but_no_result_or_links()
    {
        var source = Source.Replace("Task.FromResult(new Booking(\"b\"))", "Task.FromException<Booking>(new System.InvalidOperationException(\"failed\"))", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(Raun.Model.StepStatus.Failed, book.Status);
        Assert.Equal([ResourceEffect.Read, ResourceEffect.Update], book.Effects.Select(effect => effect.Effect));
        Assert.Empty(book.Links);
    }

    [Fact]
    public async Task Collection_inputs_record_each_target_once_without_phantom_effects()
    {
        var source = Source.Replace("[Booked.Patient] Patient patient", "[Booked.Patient] Patient[] patient", StringComparison.Ordinal)
            .Replace("Given.Book(patient, slot)", "Given.Book(new[] { patient }, slot)", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(Raun.Model.StepStatus.Passed, book.Status);
        Assert.Equal(3, book.Effects.Count);
        Assert.Equal(2, book.Links.Count);
    }

    [Fact]
    public async Task Two_roles_can_name_one_identity_without_merging_the_links()
    {
        var source = Source.Replace("Slot, Reserved>", "Patient, ReadAttribute>", StringComparison.Ordinal)
            .Replace("[Booked.Slot] Slot slot", "[Booked.Slot] Patient slot", StringComparison.Ordinal)
            .Replace("Given.Book(patient, slot)", "Given.Book(patient, patient)", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(2, book.Effects.Count);
        Assert.Equal(["Patient", "Slot"], book.Links.Select(link => link.Role));
        Assert.Equal(book.Links[0].Target, book.Links[1].Target);
    }

    [Fact]
    public async Task Packaged_exclusive_input_conflicts_with_parallel_read()
    {
        var source = Source.Replace("public sealed class Operations : Given<NoWorld> {", "public sealed class Operations : Given<NoWorld> { public Task Observe([Read] Slot slot) => Task.CompletedTask;", StringComparison.Ordinal)
            .Replace("await Given.Book(patient, slot);", "await (Given.Book(patient, slot), Given.Observe(slot));", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), diagnostic => diagnostic.Id == "RAUN013");
    }

    [Fact]
    public async Task Generic_resource_roles_are_valid_when_the_owner_is_closed_at_use()
    {
        var source = Source.Replace("class Booked :", "class Booked<T> :", StringComparison.Ordinal)
            .Replace("[return: Booked]", "[return: Booked<string>]", StringComparison.Ordinal)
            .Replace("[Booked.Patient]", "[Booked<string>.Patient]", StringComparison.Ordinal)
            .Replace("[Booked.Slot]", "[Booked<string>.Slot]", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
    }

    [Fact]
    public async Task Canonical_role_matching_follows_transitive_alternate_groups()
    {
        var source = Source.Replace("public record Patient(string Id) : IResource<Patient>",
            "public record Response(string Id); public record Row(string Id) : IResourceAlternate<Response> { public static ResourceKey KeyFor(Response value) => value.Id; } public record Patient(string Id) : IResource<Patient>, IResourceAlternate<Row>", StringComparison.Ordinal)
            .Replace("public static ResourceKey KeyFor(Patient value) => value.Id;", "public static ResourceKey KeyFor(Patient value) => value.Id; public static ResourceKey KeyFor(Row value) => value.Id;", StringComparison.Ordinal)
            .Replace("Task<Patient> Patient() => Task.FromResult(new Patient", "Task<Response> Patient() => Task.FromResult(new Response", StringComparison.Ordinal)
            .Replace("[Booked.Patient] Patient patient", "[Booked.Patient] Response patient", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(r => r.Node.OperationName == "Book");
        Assert.Equal(Raun.Model.StepStatus.Passed, book.Status);
        Assert.Equal("Patient", Assert.Single(book.Links, link => link.Role == "Patient").Target.Type!.Name);
    }

    [Fact]
    public void Editor_facade_distinguishes_absent_valid_competing_and_invalid_actions()
    {
        var compilation = GeneratorHarness.CompilationFor(Source, new CSharpParseOptions(LanguageVersion.Preview));
        var method = compilation.GetTypeByMetadataName("Operations")!.GetMembers("Book").OfType<Microsoft.CodeAnalysis.IMethodSymbol>().Single();
        Assert.Equal(ResourceActionBindingStatus.Valid, ResourceCodeModel.GetParameterAction(compilation, method.Parameters[1], out var reserved));
        Assert.Equal("Update", reserved!.Effect);
        var invalid = GeneratorHarness.CompilationFor(Source.Replace("DisplayName(\"Reserve\")", "DisplayName(\" \")", StringComparison.Ordinal), new CSharpParseOptions(LanguageVersion.Preview));
        var invalidMethod = invalid.GetTypeByMetadataName("Operations")!.GetMembers("Book").OfType<Microsoft.CodeAnalysis.IMethodSymbol>().Single();
        Assert.Equal(ResourceActionBindingStatus.Invalid, ResourceCodeModel.GetParameterAction(invalid, invalidMethod.Parameters[1], out _));
        var competing = GeneratorHarness.CompilationFor(Source.Replace("[Booked.Patient]", "[Booked.Patient, Read]", StringComparison.Ordinal), new CSharpParseOptions(LanguageVersion.Preview));
        var competingMethod = competing.GetTypeByMetadataName("Operations")!.GetMembers("Book").OfType<Microsoft.CodeAnalysis.IMethodSymbol>().Single();
        Assert.Equal(ResourceActionBindingStatus.Invalid, ResourceCodeModel.GetParameterAction(competing, competingMethod.Parameters[0], out _));
        var absent = GeneratorHarness.CompilationFor(Source.Replace("[Booked.Patient]", "", StringComparison.Ordinal), new CSharpParseOptions(LanguageVersion.Preview));
        var absentMethod = absent.GetTypeByMetadataName("Operations")!.GetMembers("Book").OfType<Microsoft.CodeAnalysis.IMethodSymbol>().Single();
        Assert.Equal(ResourceActionBindingStatus.None, ResourceCodeModel.GetParameterAction(absent, absentMethod.Parameters[0], out _));
    }

    [Fact]
    public async Task Explicit_links_allow_external_types_configured_by_a_custom_host()
    {
        var source = Source.Replace("public record Patient(string Id)", "public record Row(string Id); public record Patient(string Id)", StringComparison.Ordinal)
            .Replace("Task<Patient> Patient() => Task.FromResult(new Patient", "Task<Row> Patient() => Task.FromResult(new Row", StringComparison.Ordinal)
            .Replace("[Booked.Patient] Patient patient", "[Booked.Patient] Row patient", StringComparison.Ordinal)
            + """
            public sealed class Services : System.IServiceProvider {
                private readonly ResourceIdentityResolver resolver = new();
                public Services() { resolver.Identify<Patient>().With<Row>(row => row.Id); }
                public object? GetService(System.Type type) => type == typeof(ResourceIdentityResolver) ? resolver : null;
            }
            """;
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var services = (IServiceProvider)Activator.CreateInstance(generated.Assembly!.GetType("Services")!)!;
        var results = await new Raun.Scheduling.ScenarioScheduler().RunAsync(Assert.Single(generated.Definitions()), services);
        Assert.All(results, result => Assert.Equal(Raun.Model.StepStatus.Passed, result.Status));
        Assert.Equal("Patient", Assert.Single(results.Single(result => result.Node.OperationName == "Book").Links, link => link.Role == "Patient").Target.Type!.Name);
    }

    [Fact]
    public async Task Multiple_owner_applications_require_explicit_source_and_keep_both_link_sets()
    {
        var source = Source.Replace("public sealed class Operations : Given<NoWorld> {",
            "public sealed class Operations : Given<NoWorld> { [return: Created] public Task<Booking> Existing() => Task.FromResult(new Booking(\"old\"));", StringComparison.Ordinal)
            .Replace("Book([Booked.Patient] Patient patient, [Booked.Slot] Slot slot)",
                "Book([Booked] Booking prior, [Booked.Patient] Patient patient, [Booked.Slot] Slot slot)", StringComparison.Ordinal)
            .Replace("await Given.Book(patient, slot);", "var prior = await Given.Existing(); await Given.Book(prior, patient, slot);", StringComparison.Ordinal);
        Assert.Contains(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), diagnostic => diagnostic.Id == "RAUN032");
        source = source.Replace("[Booked.Patient]", "[Read, Link<Booked>(\"Patient\", Source = nameof(prior)), Link<Booked>(\"Patient\", Source = Subject.Return)]", StringComparison.Ordinal)
            .Replace("[Booked.Slot]", "[Reserved, Link<Booked>(\"Slot\", Source = nameof(prior)), Link<Booked>(\"Slot\", Source = Subject.Return)]", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source); generated.AssertCompiles();
        var book = (await Assert.Single(generated.Definitions()).RunAsync()).Single(result => result.Node.OperationName == "Book");
        Assert.Equal(Raun.Model.StepStatus.Passed, book.Status);
        Assert.Equal(4, book.Effects.Count);
        Assert.Equal(4, book.Links.Count);
        Assert.Equal(["old", "b"], book.Links.Select(link => link.Source.Key.Value).Distinct());
        Assert.All(book.Links.GroupBy(link => link.Source), group => Assert.Equal(["Patient", "Slot"], group.Select(link => link.Role)));
    }

    internal const string Source = """
        using System.Threading.Tasks; using System.ComponentModel; using Raun;
        public record Patient(string Id) : IResource<Patient> { public static ResourceKey KeyFor(Patient value) => value.Id; }
        public record Slot(string Id) : IResource<Slot> { public static ResourceKey KeyFor(Slot value) => value.Id; }
        public record Booking(string Id) : IResource<Booking> { public static ResourceKey KeyFor(Booking value) => value.Id; }
        [DisplayName("Reserve")] public sealed class Reserved : UpdatedAttribute;
        public sealed class Booked : CreatedAttribute {
          public sealed class PatientAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
          public sealed class SlotAttribute : ResourceLinkAttribute<Slot, Reserved>;
        }
        public sealed class Operations : Given<NoWorld> {
          [return: Seed] public Task<Patient> Patient() => Task.FromResult(new Patient("p"));
          [return: Seed] public Task<Slot> Slot() => Task.FromResult(new Slot("s"));
          [return: Booked] public Task<Booking> Book([Booked.Patient] Patient patient, [Booked.Slot] Slot slot) => Task.FromResult(new Booking("b"));
        }
        public sealed class Stories : Scenarios<NoWorld> {
          public Operations Given => Steps<Operations>();
          [Scenario] public async Task Story() { var patient = await Given.Patient(); var slot = await Given.Slot(); await Given.Book(patient, slot); }
        }
        """;
}
