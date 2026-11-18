using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp;

namespace Raun.Coverage.Projects;

internal sealed record SourceProject(Project Project, Compilation Compilation, string OutputPath, bool IsVerified = true)
{
    public string Name => Project.Name;
}

internal sealed class SourceProjectLoadResult(
    MSBuildWorkspace workspace,
    IReadOnlyList<SourceProject> projects,
    IReadOnlyList<string> diagnostics) : IDisposable
{
    public IReadOnlyList<SourceProject> Projects { get; } = projects;
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
    public void Dispose() => workspace.Dispose();
}

internal static class SourceProjectLoader
{
    public static Task<SourceProjectLoadResult> LoadAsync(string solutionPath, CollectorScope scope, CancellationToken cancellationToken,
        string? entryAssemblyPath = null, string? invocationDirectory = null)
    {
        WorkspaceSdk.EnsureRegistered(Path.GetDirectoryName(solutionPath)!);
        return LoadWorkspaceAsync(solutionPath, scope, entryAssemblyPath ?? Assembly.GetEntryAssembly()?.Location, invocationDirectory, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<SourceProjectLoadResult> LoadWorkspaceAsync(
        string solutionPath, CollectorScope scope, string? entryAssemblyPath, string? invocationDirectory, CancellationToken cancellationToken)
    {
        var configuration = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = configuration });
        var diagnostics = new List<string>();
        try
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var projects = new List<SourceProject>();
            var root = FindRoot(solution, entryAssemblyPath, invocationDirectory);
            diagnostics.Add("Source scope: runtime dependencies of " + root.Name);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var pending = new Queue<Project>();
            var visited = new Dictionary<string, Project>(comparer);
            var runtimeDirectory = string.IsNullOrEmpty(entryAssemblyPath) ? null : Path.GetDirectoryName(Path.GetFullPath(entryAssemblyPath));
            pending.Enqueue(root);
            while (pending.TryDequeue(out var project))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(project.FilePath!);
                if (visited.TryGetValue(path, out var existing))
                {
                    // A project built for several frameworks, reached through projects of different
                    // frameworks. One build of it is loaded when the tests run; the one met first stands
                    // in for it here (the runtime's own build, when the output directory could tell).
                    if (!existing.Id.Equals(project.Id))
                    {
                        var note = existing.Name + ": also reached as compiled variant " + project.Name + "; only " + existing.Name + " is analysed";
                        if (!diagnostics.Contains(note, StringComparer.Ordinal)) { diagnostics.Add(note); }
                    }
                    continue;
                }
                visited.Add(path, project);
                if (project.Language != LanguageNames.CSharp) { continue; }
                var properties = await ReadPropertiesAsync(project, configuration, cancellationToken).ConfigureAwait(false);
                foreach (var reference in properties.References)
                {
                    var current = workspace.CurrentSolution;
                    var referenced = project.ProjectReferences.Select(item => current.GetProject(item.ProjectId))
                        .Where(item => item?.FilePath is not null && comparer.Equals(Path.GetFullPath(item.FilePath), reference)).ToArray();
                    var candidates = current.Projects.Where(item => item.FilePath is not null
                        && comparer.Equals(Path.GetFullPath(item.FilePath), reference)).ToArray();
                    if (RuntimeVariant(candidates, runtimeDirectory) is { } loadedAtRuntime) { pending.Enqueue(loadedAtRuntime); }
                    else if (referenced.Length == 1) { pending.Enqueue(referenced[0]!); }
                    else if (candidates.Length == 0) { pending.Enqueue(await workspace.OpenProjectAsync(reference, cancellationToken: cancellationToken).ConfigureAwait(false)); }
                    else
                    {
                        // Nothing says which build runs: take the first, and say so.
                        pending.Enqueue(candidates[0]);
                        if (candidates.Length > 1) { diagnostics.Add(candidates[0].Name + ": chosen among " + candidates.Length + " compiled variants of " + Path.GetFileName(reference)); }
                    }
                }
                // Exclusions apply to the assembly, not to the dependency graph below it.
                var outputPath = project.OutputFilePath ?? properties.OutputPath;
                if ((!scope.IncludeTestAssembly && properties.IsTestProject) || !scope.IncludesModule(outputPath))
                {
                    diagnostics.Add(project.Name + ": excluded by coverage settings");
                    continue;
                }
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    diagnostics.Add(project.Name + ": compilation unavailable");
                    continue;
                }
                if (!scope.IncludesAssembly(compilation.Assembly) || scope.IsExcluded(compilation.Assembly, false))
                {
                    diagnostics.Add(project.Name + ": excluded by coverage settings");
                    continue;
                }
                var hasErrors = compilation.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error);
                projects.Add(new SourceProject(project, compilation, outputPath, !hasErrors));
                if (hasErrors)
                {
                    diagnostics.Add(project.Name + ": compilation has errors; all outcomes remain unverified");
                }
            }
            diagnostics.AddRange(workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).Select(d => d.Message));
            return new SourceProjectLoadResult(workspace, projects, diagnostics);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Among the compiled variants of one project (one per target framework), the one whose assembly
    /// is the file in the test application's output directory: the build that is loaded when the
    /// tests run. Null when there is one variant or none, or when the output directory cannot tell
    /// (nothing built, or the assembly is not copied there).
    /// </summary>
    private static Project? RuntimeVariant(Project[] candidates, string? runtimeDirectory)
    {
        if (candidates.Length < 2 || runtimeDirectory is null)
        {
            return null;
        }

        var matches = candidates.Where(candidate => candidate.OutputFilePath is { } output
            && SameFile(output, Path.Combine(runtimeDirectory, Path.GetFileName(output)))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool SameFile(string left, string right)
    {
        try
        {
            var first = new FileInfo(left);
            var second = new FileInfo(right);
            return first.Exists && second.Exists && first.Length == second.Length
                && System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(left)).AsSpan()
                    .SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(right)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Project FindRoot(Solution solution, string? entryAssemblyPath, string? invocationDirectory)
    {
        var candidates = solution.Projects.Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null).ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!string.IsNullOrEmpty(entryAssemblyPath))
        {
            var matches = candidates.Where(p => p.OutputFilePath is not null
                && comparer.Equals(Path.GetFullPath(p.OutputFilePath), Path.GetFullPath(entryAssemblyPath))).ToArray();
            if (matches.Length == 1) { return matches[0]; }
            matches = candidates.Where(p => comparer.Equals(Path.GetFileName(p.OutputFilePath), Path.GetFileName(entryAssemblyPath))).ToArray();
            if (matches.Length == 1) { return matches[0]; }
            if (matches.Length > 1) { throw new InvalidOperationException("Several source projects match the test application assembly."); }
        }
        for (var directory = invocationDirectory is null ? null : new DirectoryInfo(invocationDirectory); directory is not null; directory = directory.Parent)
        {
            var matches = candidates.Where(p => comparer.Equals(Path.GetDirectoryName(Path.GetFullPath(p.FilePath!)), directory.FullName)).ToArray();
            if (matches.Length == 1) { return matches[0]; }
            if (matches.Length > 1) { throw new InvalidOperationException("Several source projects match the invocation directory."); }
        }
        throw new InvalidOperationException("The test application's source project could not be identified; coverage scope is unavailable.");
    }

    private sealed record ProjectProperties(bool IsTestProject, string OutputPath, IReadOnlyList<string> References);

    private static async Task<ProjectProperties> ReadPropertiesAsync(Project project, string configuration, CancellationToken cancellationToken, string? targetFramework = null)
    {
        var projectPath = project.FilePath!;
        var start = DotnetProcess.StartInfo(Path.GetDirectoryName(projectPath)!);
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-getProperty:IsTestProject,IsTestingPlatformApplication,TargetPath,TargetFramework,TargetFrameworks");
        start.ArgumentList.Add("-property:Configuration=" + configuration);
        start.ArgumentList.Add("-getItem:ProjectReference");
        if (targetFramework is not null) { start.ArgumentList.Add("-property:TargetFramework=" + targetFramework); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not evaluate source project properties.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not left behind: a cancelled or timed-out analysis must not keep msbuild nodes running.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already exited
            throw;
        }
        if (process.ExitCode != 0)
        {
            // MSBuild reports its errors on standard output; standard error is usually empty.
            var said = (await output.ConfigureAwait(false) + Environment.NewLine + await error.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException(
                $"{project.Name}: dotnet msbuild exited {process.ExitCode} while reading project properties. {said}");
        }
        using var json = JsonDocument.Parse(await output.ConfigureAwait(false));
        var properties = json.RootElement.GetProperty("Properties");
        var frameworks = properties.GetProperty("TargetFrameworks").GetString()?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (targetFramework is null && string.IsNullOrEmpty(properties.GetProperty("TargetFramework").GetString()) && frameworks.Length > 0)
        {
            var components = (project.OutputFilePath ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var matches = frameworks.Where(framework => components.Contains(framework, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                var symbols = (project.ParseOptions as CSharpParseOptions)?.PreprocessorSymbolNames ?? [];
                matches = frameworks.Where(framework => symbols.Contains(framework.Split('-')[0].ToUpperInvariant().Replace('.', '_'), StringComparer.Ordinal)).ToArray();
            }
            if (matches.Length != 1) { throw new InvalidOperationException(project.Name + ": target framework could not be identified for runtime references."); }
            return await ReadPropertiesAsync(project, configuration, cancellationToken, matches[0]).ConfigureAwait(false);
        }
        var references = json.RootElement.GetProperty("Items").GetProperty("ProjectReference")
            .EnumerateArray().Where(item => !string.Equals(Metadata(item, "OutputItemType"), "Analyzer", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(Metadata(item, "ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Metadata(item, "IsAspireProjectResource"), "true", StringComparison.OrdinalIgnoreCase)))
            .Select(item => Path.GetFullPath(Metadata(item, "FullPath") ?? Path.Combine(Path.GetDirectoryName(projectPath)!, item.GetProperty("Identity").GetString()!))).ToArray();
        return new ProjectProperties(string.Equals(properties.GetProperty("IsTestProject").GetString(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(properties.GetProperty("IsTestingPlatformApplication").GetString(), "true", StringComparison.OrdinalIgnoreCase),
            properties.GetProperty("TargetPath").GetString() ?? "", references);
    }

    private static string? Metadata(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) ? value.GetString() : null;
}
