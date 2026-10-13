using Microsoft.CodeAnalysis.MSBuild;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverageEnvironmentProbeTests
{
    [Fact]
    public async Task Loads_solution_with_pinned_sdk_and_evaluated_source_items()
    {
        var root = FindRoot();
        var sdk = WorkspaceSdk.EnsureRegistered(root);
        Assert.Equal(10, sdk.Major);
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(Path.Combine(root, "Raun.slnx"), cancellationToken: TestContext.Current.CancellationToken);
        var api = Assert.Single(solution.Projects, p => p.Name == "AspireAppointments.Api");
        var compilation = await api.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(compilation);
        Assert.Contains(api.Documents, d => d.Name == "Program.cs");
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Equal(sdk, WorkspaceSdk.EnsureRegistered(root));
    }

    internal static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Raun.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
}
