using System.Diagnostics;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class RuntimeScopeFixtureTests
{
    [Fact]
    public async Task Multi_target_neighbors_do_not_expand_or_break_the_runtime_scope()
    {
        var root = Path.Combine(Path.GetTempPath(), "raun-runtime-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(Path.Combine(CoverageEnvironmentProbeTests.FindRoot(), "global.json"), Path.Combine(root, "global.json"));
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            WriteProject(root, "Root", "<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject>",
                "<ProjectReference Include='../Sut/Sut.csproj' />", "class Program { static void Main() {} }");
            WriteProject(root, "Sut", "<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>",
                "<ProjectReference Include='../Dependency/Dependency.csproj' Condition=\"'$(TargetFramework)' == 'net10.0'\" />",
                "public static class Sut { public static int M(bool b) => b ? 1 : 2; }");
            WriteProject(root, "Dependency", "<TargetFramework>net10.0</TargetFramework>", "",
                "public static class Dependency { public static int NeverCalled(bool b) => b ? 1 : 2; }");
            WriteProject(root, "Unrelated", "<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>", "", "public class Unrelated {}");
            var solution = Path.Combine(root, "Scope.slnx");
            File.WriteAllText(solution, "<Solution><Project Path='Root/Root.csproj'/><Project Path='Sut/Sut.csproj'/><Project Path='Dependency/Dependency.csproj'/><Project Path='Unrelated/Unrelated.csproj'/></Solution>");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("restore"); start.ArgumentList.Add(solution);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await error);
            using var loaded = await SourceProjectLoader.LoadAsync(solution, new CollectorScope(), TestContext.Current.CancellationToken,
                Path.Combine(root, "Root", "bin", "Debug", "net10.0", "Root.dll"));
            Assert.Equal(["Dependency", "Sut"], loaded.Projects.Select(p => Path.GetFileNameWithoutExtension(p.Project.FilePath)).Order(StringComparer.Ordinal));
            Assert.All(loaded.Projects, project => Assert.True(project.IsVerified));
            Assert.All(loaded.Projects, project => Assert.Contains("net10.0", project.OutputPath, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WriteProject(string root, string name, string properties, string references, string source)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup>" + properties + "</PropertyGroup><ItemGroup>" + references + "</ItemGroup></Project>");
        File.WriteAllText(Path.Combine(directory, "Code.cs"), source);
    }
}
