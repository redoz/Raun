using System.Diagnostics;
using System.Reflection;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

/// <summary>
/// A library built for several frameworks, reached twice from the test project: directly through a
/// project of the test's own framework, and through a netstandard project. The solution then holds
/// two compiled variants of the one library. Only one of them is loaded when the tests run, so the
/// source scope has one answer and must not give up.
/// </summary>
public sealed class RuntimeVariantFixtureTests
{
    [Fact]
    public async Task A_library_reached_as_two_compiled_variants_is_analysed_once()
    {
        using var fixture = await Fixture.CreateAsync(build: false);

        using var loaded = await fixture.LoadAsync();

        Assert.Equal(["A", "B", "Lib"], loaded.Projects.Select(p => Path.GetFileNameWithoutExtension(p.Project.FilePath)).Order(StringComparer.Ordinal));
        Assert.Contains(loaded.Diagnostics, line =>
            line.Contains("Lib", StringComparison.Ordinal) && line.Contains("variant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_variant_analysed_is_the_one_the_test_application_runs_with()
    {
        // B is listed first and is netstandard, so the netstandard build of Lib is met first. The
        // test application's output directory holds the net10.0 build: that is what runs.
        using var fixture = await Fixture.CreateAsync(build: true);

        using var loaded = await fixture.LoadAsync();

        var lib = Assert.Single(loaded.Projects, p => Path.GetFileNameWithoutExtension(p.Project.FilePath) == "Lib");
        Assert.Contains("net10.0", lib.OutputPath, StringComparison.Ordinal);
    }

    private sealed class Fixture(string root) : IDisposable
    {
        // The loader reads projects in the configuration of the application it runs in, which here
        // is this test assembly: the fixture has to be built in that one for its output to be found.
        private static readonly string Configuration =
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

        public static async Task<Fixture> CreateAsync(bool build)
        {
            var root = Path.Combine(Path.GetTempPath(), "raun-runtime-variant-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root);
            try
            {
                File.Copy(Path.Combine(CoverageEnvironmentProbeTests.FindRoot(), "global.json"), Path.Combine(root, "global.json"));
                File.WriteAllText(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
                WriteProject(root, "Root", "<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject>",
                    "<ProjectReference Include='../B/B.csproj' /><ProjectReference Include='../A/A.csproj' />",
                    "class Program { static void Main() { System.Console.WriteLine(A.M(true) + B.M(true)); } }");
                WriteProject(root, "A", "<TargetFramework>net10.0</TargetFramework>", "<ProjectReference Include='../Lib/Lib.csproj' />",
                    "public static class A { public static int M(bool b) => Lib.M(b); }");
                WriteProject(root, "B", "<TargetFramework>netstandard2.0</TargetFramework>", "<ProjectReference Include='../Lib/Lib.csproj' />",
                    "public static class B { public static int M(bool b) => Lib.M(b); }");
                WriteProject(root, "Lib", "<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>", "",
                    "public static class Lib { public static int M(bool b) => b ? 1 : 2; }");
                File.WriteAllText(fixture.Solution, "<Solution><Project Path='Root/Root.csproj'/><Project Path='A/A.csproj'/><Project Path='B/B.csproj'/><Project Path='Lib/Lib.csproj'/></Solution>");
                await DotnetAsync(root, "restore", fixture.Solution);
                if (build)
                {
                    await DotnetAsync(root, "build", Path.Combine(root, "Root", "Root.csproj"), "-c", Configuration, "--no-restore", "-v", "q", "--nologo");
                }

                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        private string Solution => Path.Combine(root, "Variant.slnx");

        public Task<SourceProjectLoadResult> LoadAsync()
            => SourceProjectLoader.LoadAsync(Solution, new CollectorScope(), TestContext.Current.CancellationToken,
                Path.Combine(root, "Root", "bin", Configuration, "net10.0", "Root.dll"));

        public void Dispose()
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }

        private static async Task DotnetAsync(string root, params string[] arguments)
        {
            var start = DotnetProcess.StartInfo(root);
            foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await error);
        }

        private static void WriteProject(string root, string name, string properties, string references, string source)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".csproj"), "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup>" + properties + "</PropertyGroup><ItemGroup>" + references + "</ItemGroup></Project>");
            File.WriteAllText(Path.Combine(directory, "Code.cs"), source);
        }
    }
}
