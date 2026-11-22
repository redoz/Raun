using System.Diagnostics.CodeAnalysis;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.TestHostControllers;

namespace Raun.Mtp.Coverage;

/// <summary>
/// Carries the run's hosted source projects from the test host to the process that started it.
/// The host is where the run learns them (an AppHost is built in preflight); Coverlet's report is
/// analysed in the controller, after the host has exited. The controller names a file in the
/// host's environment, the host leaves the list there as it ends, and the controller takes it.
/// </summary>
internal sealed class HostedProjectsHandoff : ITestHostEnvironmentVariableProvider
{
    internal const string Variable = "RAUN_COVERAGE_SOURCE_PROJECTS_FILE";

    private readonly string _file = Path.Combine(Path.GetTempPath(), "raun-coverage-sources-" + Guid.NewGuid().ToString("N") + ".txt");

    public string Uid => "raun.mtp.coveragesourceprojects";
    public string Version => "1.0.0";
    public string DisplayName => "Raun coverage source projects";
    public string Description => "Tells the test host where to leave the list of source projects the run hosted.";
    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task UpdateAsync(IEnvironmentVariables environmentVariables)
    {
        environmentVariables.SetVariable(new EnvironmentVariable(Variable, _file, isSecret: false, isLocked: true));
        return Task.CompletedTask;
    }

    public Task<ValidationResult> ValidateTestHostEnvironmentVariablesAsync(IReadOnlyEnvironmentVariables environmentVariables)
        => ValidationResult.ValidTask;

    /// <summary>In the test host: leaves the list where the controller asked for it. Nothing to do
    /// when no controller asked, or when the run hosted nothing.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Coverage scope is best effort and must not change the outcome of a finished run.")]
    public static void Leave(IReadOnlyList<string> projects)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } file || projects.Count == 0)
        {
            return;
        }

        try { File.WriteAllLines(file, projects); }
        catch (Exception error) { Raun.Diagnostics.DebugTrace.Write("coverage-handoff-failure", error.ToString()); }
    }

    /// <summary>In the controller: the list the host left, removed once read. Empty when the host
    /// left none.</summary>
    public IReadOnlyList<string> Take() => Take(_file);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Coverage scope is best effort and must not change the outcome of a finished run.")]
    internal static IReadOnlyList<string> Take(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return [];
            }

            var projects = File.ReadAllLines(file).Where(line => line.Length > 0).ToArray();
            File.Delete(file);
            return projects;
        }
        catch (Exception error)
        {
            Raun.Diagnostics.DebugTrace.Write("coverage-handoff-failure", error.ToString());
            return [];
        }
    }
}
