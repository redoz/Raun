using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.TestHostControllers;

namespace Raun.Mtp.Coverage;

/// <summary>
/// Carries the run's hosted source projects from the test host to the process that started it.
/// The host is where the run learns them (an AppHost is built in preflight); Coverlet's report is
/// analysed in the controller, after the host has exited.
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

    public static string? FileNamedByController
        => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } file ? file : null;

    public static void Leave(string file, IReadOnlyList<string> projects)
    {
        if (projects.Count == 0)
        {
            return;
        }

        try { File.WriteAllLines(file, projects); }
        catch (IOException error) { NotHandedOver(error); }
        catch (UnauthorizedAccessException error) { NotHandedOver(error); }
    }

    public IReadOnlyList<string> Take() => Take(_file);

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
        catch (IOException error) { return NotHandedOver(error); }
        catch (UnauthorizedAccessException error) { return NotHandedOver(error); }
    }

    private static IReadOnlyList<string> NotHandedOver(Exception error)
    {
        Raun.Diagnostics.DebugTrace.Write("coverage-handoff-failure", error.ToString());
        return [];
    }
}
