using System.Diagnostics;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

/// <summary>
/// Source that runs as part of the test run but in another process, and that nothing in the test
/// project's references leads to: an API an Aspire AppHost starts by path. The run names such
/// projects; the reference graph cannot.
/// </summary>
public sealed class HostedProjectFixtureTests
{
    [Fact]
    public async Task A_project_the_run_hosts_is_analysed_with_what_it_references_though_nothing_references_it()
    {
        using var fixture = await Fixture.CreateAsync();

        using var loaded = await SourceProjectLoader.LoadAsync(fixture.Solution, new CollectorScope(), TestContext.Current.CancellationToken,
            fixture.EntryAssembly, hostedProjects: [fixture.Project("Service")]);

        Assert.Equal(["Linked", "Service", "ServiceLib"], Names(loaded));
        Assert.Equal(["Service"], loaded.Projects.Where(project => project.IsHosted).Select(project => project.Name));
        Assert.Contains(loaded.Diagnostics, line => line.Contains("Service", StringComparison.Ordinal) && line.Contains("hosted by the run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reference_that_links_nothing_does_not_put_a_project_in_scope()
    {
        using var fixture = await Fixture.CreateAsync();

        // Root references Service the way an AppHost used to reference what it starts: built, not
        // linked. That says nothing about what runs, so it is not followed.
        using var loaded = await SourceProjectLoader.LoadAsync(fixture.Solution, new CollectorScope(), TestContext.Current.CancellationToken,
            fixture.EntryAssembly);

        Assert.Equal(["Linked"], Names(loaded));
    }

    [Fact]
    public async Task A_hosted_project_the_collector_saw_nothing_of_is_a_warning_and_a_linked_one_is_not()
    {
        using var fixture = await Fixture.CreateAsync();
        var coverage = Path.Combine(fixture.Root, "coverage.xml");
        File.WriteAllText(coverage, "<results><modules /></results>");

        var analysis = await ClassifiedCoverageAnalyzer.AnalyzeWithWarningsAsync(coverage, Path.Combine(fixture.Root, "Root"), new CollectorScope(),
            [], [fixture.Project("Service")], TestContext.Current.CancellationToken);

        var warning = Assert.Single(analysis.Warnings);
        Assert.Contains("Service", warning, StringComparison.Ordinal);
        Assert.Contains("no coverage", warning, StringComparison.OrdinalIgnoreCase);
        // The warning is in the report too, where the numbers it qualifies are read.
        Assert.Contains(warning, analysis.Report.Diagnostics);
        Assert.Contains(analysis.Report.Diagnostics, line => line.StartsWith("Linked: no execution evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(analysis.Warnings, line => line.Contains("Linked", StringComparison.Ordinal));
    }

    private static IEnumerable<string> Names(SourceProjectLoadResult loaded)
        => loaded.Projects.Select(project => Path.GetFileNameWithoutExtension(project.Project.FilePath)!).Order(StringComparer.Ordinal);

    /// <summary>
    /// Root (the test application) links Linked and references Service without linking it. Service
    /// references ServiceLib and is not in the solution, as a project added by path need not be.
    /// </summary>
    private sealed class Fixture(string root) : IDisposable
    {
        public string Root { get; } = root;
        public string Solution => Path.Combine(Root, "Hosted.slnx");
        public string EntryAssembly => Path.Combine(Root, "Root", "bin", "Debug", "net10.0", "Root.dll");
        public string Project(string name) => Path.Combine(Root, name, name + ".csproj");

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(Path.Combine(Path.GetTempPath(), "raun-hosted-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture.Root);
            File.Copy(Path.Combine(CoverageEnvironmentProbeTests.FindRoot(), "global.json"), Path.Combine(fixture.Root, "global.json"));
            File.WriteAllText(Path.Combine(fixture.Root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            fixture.Write("Root", "<OutputType>Exe</OutputType><IsTestProject>true</IsTestProject>",
                "<ProjectReference Include='../Linked/Linked.csproj' />"
                + "<ProjectReference Include='../Service/Service.csproj' ReferenceOutputAssembly='false' IsAspireProjectResource='true' />",
                "class Program { static void Main() {} }");
            fixture.Write("Linked", "", "", "public static class Linked { public static int M(bool b) => b ? 1 : 2; }");
            fixture.Write("Service", "<OutputType>Exe</OutputType>", "<ProjectReference Include='../ServiceLib/ServiceLib.csproj' />",
                "class Program { static int Main(string[] args) => ServiceLib.M(args.Length == 0); }");
            fixture.Write("ServiceLib", "", "", "public static class ServiceLib { public static int M(bool b) => b ? 1 : 2; }");
            File.WriteAllText(fixture.Solution, "<Solution><Project Path='Root/Root.csproj'/><Project Path='Linked/Linked.csproj'/><Project Path='ServiceLib/ServiceLib.csproj'/></Solution>");
            await fixture.RestoreAsync(fixture.Solution);
            await fixture.RestoreAsync(fixture.Project("Service"));
            return fixture;
        }

        private void Write(string name, string properties, string references, string source)
        {
            var directory = Path.Combine(Root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".csproj"),
                "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework>" + properties + "</PropertyGroup><ItemGroup>" + references + "</ItemGroup></Project>");
            File.WriteAllText(Path.Combine(directory, "Code.cs"), source);
        }

        private async Task RestoreAsync(string target)
        {
            var start = DotnetProcess.StartInfo(Root);
            start.ArgumentList.Add("restore");
            start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await error);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
