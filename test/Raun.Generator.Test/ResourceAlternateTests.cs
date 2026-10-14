using Microsoft.CodeAnalysis;
using Xunit;

namespace Raun.Generator.Test;

public sealed class ResourceAlternateTests
{
    [Fact]
    public void Local_defaults_are_attached_to_every_definition_without_custom_startup()
    {
        var result = GeneratorHarness.Run(Declarations + Story);
        result.AssertCompiles();
        var definition = Assert.Single(result.Definitions());
        var mapping = Assert.Single(definition.ResourceMappings);
        Assert.Equal("Patient", mapping.OwnerType.Name);
        Assert.Equal("Row", mapping.SourceType.Name);
        var resolver = new ResourceIdentityResolver();
        resolver.ApplyMappings(definition.ResourceMappings);
        var row = Activator.CreateInstance(mapping.SourceType, "42", "Jane")!;
        Assert.Equal(new ResourceIdentity(mapping.OwnerType, "42"), resolver.Resolve(mapping.SourceType, row));
        Assert.Equal("Jane", Label(resolver, row, mapping.SourceType));
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(Declarations + Story));
    }

    [Fact]
    public void Referenced_mapping_only_library_supports_internal_owners_without_global_leaks()
    {
        var library = GeneratorHarness.Run(Declarations, "Mappings");
        library.AssertCompiles();
        var reference = MetadataReference.CreateFromImage(library.Image);
        var application = GeneratorHarness.Run(Story, "Consumer", reference);
        application.AssertCompiles();
        var imported = Assert.Single(Assert.Single(application.Definitions()).ResourceMappings);
        Assert.Equal(library.Assembly, imported.OwnerType.Assembly);
        var unrelated = GeneratorHarness.Run(Story, "Unrelated");
        unrelated.AssertCompiles();
        Assert.Empty(Assert.Single(unrelated.Definitions()).ResourceMappings);
    }

    [Fact]
    public void Metadata_does_not_run_owner_initialization_or_extractors()
    {
        var source = Declarations.Replace("public static ResourceKey KeyFor(Row value) => value.Id;",
            "static Patient() => throw new System.Exception(\"initialized\"); public static ResourceKey KeyFor(Row value) => throw new System.Exception(\"extracted\");", StringComparison.Ordinal);
        var library = GeneratorHarness.Run(source);
        library.AssertCompiles();
        var app = GeneratorHarness.Run(Story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        app.AssertCompiles();
        Assert.Single(Assert.Single(app.Definitions()).ResourceMappings);
    }

    [Theory]
    [InlineData("internal class Patient<T> : IResourceAlternate<Row> { public static ResourceKey KeyFor(Row value) => value.Id; }")]
    [InlineData("public class Container { private class Patient : IResourceAlternate<Row> { public static ResourceKey KeyFor(Row value) => value.Id; } }")]
    public void Unsupported_owners_are_diagnosed(string declaration)
    {
        var result = GeneratorHarness.Run("using Raun; public record Row(string Id); " + declaration);
        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "RAUN025");
    }

    [Fact]
    public void Conflicting_local_and_imported_owners_are_diagnosed()
    {
        var library = GeneratorHarness.Run(Declarations, "Mappings");
        library.AssertCompiles();
        var result = GeneratorHarness.Run("using Raun; public class Other : IResourceAlternate<Row> { public static ResourceKey KeyFor(Row row) => row.Id; }" + Story,
            "Consumer", MetadataReference.CreateFromImage(library.Image));
        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "RAUN026");
    }

    [Fact]
    public async Task Generated_host_claims_and_key_titles_use_alternate_defaults_before_execution()
    {
        var source = Declarations + """
            public sealed class Steps : Raun.Given<Raun.NoWorld>
            {
                [return: Raun.Created]
                public System.Threading.Tasks.Task<Row> Seed() => System.Threading.Tasks.Task.FromResult(new Row("42", "Jane"));
                [Raun.StepName("touch {id}")]
                public System.Threading.Tasks.Task Touch([Raun.Read, Raun.ResourceKey<Patient>] string id) => System.Threading.Tasks.Task.CompletedTask;
            }
            public sealed class Stories : Raun.Scenarios<Raun.NoWorld>
            {
                public Steps Given => Steps<Steps>();
                [Raun.Scenario] public async System.Threading.Tasks.Task Story()
                {
                    var row = await Given.Seed();
                    await Given.Touch(row.Id);
                }
            }
            """;
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(Raun.Model.StepStatus.Passed, result.Status));
        Assert.Equal(Assert.Single(results[1].Effects).Identity, Assert.Single(results[2].Effects).Identity);
        Assert.Equal("Patient", Assert.Single(results[1].Effects).Identity.Type!.Name);
        Assert.Equal("touch Patient:42", results[2].DisplayName);
    }

    [Fact]
    public async Task Referenced_library_defaults_execute_without_a_custom_resolver()
    {
        var library = GeneratorHarness.Run(Declarations, "Mappings");
        library.AssertCompiles();
        var application = GeneratorHarness.Run("""
            public sealed class Steps : Raun.Given<Raun.NoWorld>
            {
                [return: Raun.Created]
                public System.Threading.Tasks.Task<Row> Seed() => System.Threading.Tasks.Task.FromResult(new Row("42", "Jane"));
                [Raun.StepName("touch {id}")]
                public System.Threading.Tasks.Task Touch([Raun.Read, Raun.ResourceKey<Row>] string id) => System.Threading.Tasks.Task.CompletedTask;
            }
            public sealed class Stories : Raun.Scenarios<Raun.NoWorld>
            {
                public Steps Given => Steps<Steps>();
                [Raun.Scenario] public async System.Threading.Tasks.Task Story()
                {
                    var row = await Given.Seed();
                    await Given.Touch(row.Id);
                }
            }
            """, "Consumer", MetadataReference.CreateFromImage(library.Image));
        application.AssertCompiles();
        var results = await Assert.Single(application.Definitions()).RunAsync();
        Assert.All(results, result => Assert.Equal(Raun.Model.StepStatus.Passed, result.Status));
        Assert.Equal(Assert.Single(results[1].Effects).Identity, Assert.Single(results[2].Effects).Identity);
        Assert.Equal("touch Patient:42", results[2].DisplayName);
    }

    [Theory]
    [InlineData("Box<int>", "value.Id")]
    [InlineData("Row[]", "value[0].Id")]
    public void Closed_generic_and_array_sources_import_correctly(string sourceType, string key)
    {
        var library = GeneratorHarness.Run($$"""
            using Raun;
            public record Row(string Id);
            public record Box<T>(string Id);
            internal class Owner : IResourceAlternate<{{sourceType}}>
            {
                public static ResourceKey KeyFor({{sourceType}} value) => {{key}};
            }
            """, "Mappings");
        library.AssertCompiles();
        var application = GeneratorHarness.Run(Story, "Consumer", MetadataReference.CreateFromImage(library.Image));
        application.AssertCompiles();
        Assert.Single(Assert.Single(application.Definitions()).ResourceMappings);
    }

    [Fact]
    public void Inherited_contracts_do_not_declare_a_second_owner()
    {
        var result = GeneratorHarness.Run(Declarations + "internal class SpecializedPatient : Patient { }" + Story);
        result.AssertCompiles();
        Assert.Equal("Patient", Assert.Single(Assert.Single(result.Definitions()).ResourceMappings).OwnerType.Name);
    }

    [Fact]
    public void Generic_sources_with_same_named_arguments_from_distinct_assemblies_remain_distinct()
    {
        var first = GeneratorHarness.Run("namespace Shared { public record Row(string Id); }", "First");
        var second = GeneratorHarness.Run("namespace Shared { public record Row(string Id); }", "Second");
        first.AssertCompiles();
        second.AssertCompiles();
        var firstReference = MetadataReference.CreateFromImage(first.Image);
        var secondReference = MetadataReference.CreateFromImage(second.Image);
        var left = GeneratorHarness.Run("""
            using Raun; using System.Collections.Generic;
            public class One : IResourceAlternate<List<Shared.Row>>
            { public static ResourceKey KeyFor(List<Shared.Row> rows) => rows[0].Id; }
            """, "LeftMappings", firstReference);
        var right = GeneratorHarness.Run("""
            using Raun; using System.Collections.Generic;
            public class Two : IResourceAlternate<List<Shared.Row>>
            { public static ResourceKey KeyFor(List<Shared.Row> rows) => rows[0].Id; }
            """, "RightMappings", secondReference);
        left.AssertCompiles();
        right.AssertCompiles();
        var application = GeneratorHarness.Run(Story, "Consumer", firstReference, secondReference,
            MetadataReference.CreateFromImage(left.Image), MetadataReference.CreateFromImage(right.Image));
        application.AssertCompiles();
        var mappings = Assert.Single(application.Definitions()).ResourceMappings;
        Assert.Equal(2, mappings.Count);
        Assert.NotEqual(mappings[0].SourceType, mappings[1].SourceType);
    }

    [Theory]
    [InlineData("file class Container { public class Owner : IResourceAlternate<Row> { public static ResourceKey KeyFor(Row row) => row.Id; } }", "public record Row(string Id);")]
    [InlineData("file class Container { public record Row(string Id); } public class Owner : IResourceAlternate<Container.Row> { public static ResourceKey KeyFor(Container.Row row) => row.Id; }", "")]
    public void File_local_containers_are_diagnosed_at_the_declaration(string declaration, string row)
    {
        var result = GeneratorHarness.Run("using Raun; " + row + declaration);
        Assert.Contains(result.GeneratorDiagnostics, diagnostic => diagnostic.Id == "RAUN025" && diagnostic.Location.IsInSource);
        Assert.DoesNotContain("ResourceMapping.Alternate", result.GeneratedSource, StringComparison.Ordinal);
    }

    private static string Label(ResourceIdentityResolver resolver, object row, Type sourceType)
        => (string)typeof(ResourceAlternateTests).GetMethod(nameof(Format), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(sourceType).Invoke(null, [resolver, row])!;

    private static string Format<T>(ResourceIdentityResolver resolver, T row)
        => new StepMessageBuilder(new MessageFormattingContext(resolver)).AppendFormatted(row).Build().Text;

    private const string Declarations = """
        using Raun;
        public record Row(string Id, string Name);
        internal class Patient : IResourceAlternate<Row>
        {
            public static ResourceKey KeyFor(Row value) => value.Id;
            public static string? LabelFor(Row value) => value.Name;
        }
        """;

    private const string Story = """
        public sealed class Steps : Raun.Given<Raun.NoWorld>
        {
            public System.Threading.Tasks.Task Ready() => System.Threading.Tasks.Task.CompletedTask;
        }
        public sealed class Stories : Raun.Scenarios<Raun.NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Raun.Scenario] public async System.Threading.Tasks.Task Story() { await Given.Ready(); }
        }
        """;
}
