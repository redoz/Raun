using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class SourceProjectTests
{
    [Fact]
    public void Discovers_solution_from_nested_invocation_directory()
    {
        var root = CoverageEnvironmentProbeTests.FindRoot();
        var result = SolutionLocator.Resolve(Path.Combine(root, "samples", "AspireAppointments"));
        Assert.Equal(Path.Combine(root, "Raun.slnx"), result);
    }

    [Fact]
    public async Task Inventories_runtime_dependencies_and_honors_test_opt_out()
    {
        using var loaded = await SourceProjectLoader.LoadAsync(
            Path.Combine(CoverageEnvironmentProbeTests.FindRoot(), "Raun.slnx"), new CollectorScope(), TestContext.Current.CancellationToken);
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "Raun");
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Coverage");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Generator");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "AspireAppointments.Api");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "AppointmentTests");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Coverage.Test");
        // Multi-targeted projects are named per target framework, e.g. "Raun.Coverage.Test(net11.0)".
        Assert.Contains(loaded.Diagnostics, d => d.StartsWith("Raun.Coverage.Test", StringComparison.Ordinal) && d.Contains(": excluded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Includes_aspire_child_applications_but_not_analyzers_or_unrelated_projects(bool useSourceContext)
    {
        var root = CoverageEnvironmentProbeTests.FindRoot();
        var source = Path.Combine(root, "samples", "AspireAppointments", "AspireAppointments.Tests");
        using var loaded = await SourceProjectLoader.LoadAsync(Path.Combine(root, "Raun.slnx"), new CollectorScope(),
            TestContext.Current.CancellationToken,
            useSourceContext ? "ExternalConsumer.dll" : Path.Combine(source, "bin", "Debug", "net10.0", "AspireAppointments.Tests.dll"),
            useSourceContext ? source : root);
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "AspireAppointments.Api");
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "AspireAppointments.AppHost");
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Mtp");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Generator");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "AppointmentTests");
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "AspireAppointments.Tests");
    }

    [Fact]
    public async Task Opting_out_a_project_does_not_prune_its_runtime_dependencies()
    {
        var root = CoverageEnvironmentProbeTests.FindRoot();
        using var loaded = await SourceProjectLoader.LoadAsync(Path.Combine(root, "Raun.slnx"),
            new CollectorScope { ModuleExcludes = [@"Raun\.Aspire\.dll$"] }, TestContext.Current.CancellationToken,
            Path.Combine(root, "samples", "AspireAppointments", "AspireAppointments.Tests", "bin", "Debug", "net10.0", "AspireAppointments.Tests.dll"));
        Assert.DoesNotContain(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Aspire");
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "Raun.Mtp");
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "Raun");
        Assert.Contains(loaded.Projects, p => p.Compilation.AssemblyName == "AspireAppointments.Api");
    }

    [Fact]
    public async Task Missing_root_does_not_fall_back_to_the_entire_solution()
    {
        var root = CoverageEnvironmentProbeTests.FindRoot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SourceProjectLoader.LoadAsync(
            Path.Combine(root, "Raun.slnx"), new CollectorScope(), TestContext.Current.CancellationToken,
            "NoSuchApplication.dll", root));
    }

    [Fact]
    public void Equivalent_solution_formats_are_one_scope()
    {
        var root = Path.Combine(Path.GetTempPath(), "raun-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Example.slnx"), "<Solution><Project Path='A.csproj' /></Solution>");
            File.WriteAllText(Path.Combine(root, "Example.sln"), "Project(\"{guid}\") = \"A\", \"A.csproj\", \"{guid}\"\nEndProject");
            Assert.Equal(Path.Combine(root, "Example.slnx"), SolutionLocator.Resolve(root));
            File.WriteAllText(Path.Combine(root, "Other.slnx"), "<Solution><Project Path='B.csproj' /></Solution>");
            Assert.Throws<InvalidOperationException>(() => SolutionLocator.Resolve(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Exclusion_attribute_applies_to_type_members_and_assembly()
    {
        var compilation = TestCompilation.Create("""
            using System.Diagnostics.CodeAnalysis;
            [ExcludeFromCodeCoverage] class C { public void M() {} }
            """);
        var type = compilation.GetTypeByMetadataName("C")!;
        Assert.True(new CollectorScope().IsExcluded(type.GetMembers("M")[0]));
        Assert.False(new CollectorScope { MergeDefaultAttributes = false }.IsExcluded(type));
    }

    [Fact]
    public void Excludes_win_over_includes_and_keep_unobserved_modules()
    {
        var scope = new CollectorScope
        {
            ModuleIncludes = [".*"],
            ModuleExcludes = ["Hidden"],
        };
        Assert.False(scope.IncludesModule("Hidden.dll"));
        Assert.True(scope.IncludesModule("Unloaded.dll"));
    }
}

internal static class TestCompilation
{
    public static CSharpCompilation Create(string source)
        => CSharpCompilation.Create("Fixture",
            [CSharpSyntaxTree.ParseText(source, path: "fixture.cs")],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
