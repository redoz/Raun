using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Proves the platform registrations reach a real Microsoft.Testing.Platform host: <c>--help</c>
/// lists <c>--treenode-filter</c>, <c>--maximum-failed-tests</c> and <c>--filter-uid</c>, and a
/// <c>--treenode-filter</c> run against the AppointmentTests sample selects only the targeted
/// scenario's steps. Everything else in this test project exercises the filter/selector logic in
/// isolation; only spawning the sample as a real process can catch a registration that was silently
/// never wired up, which is exactly the gap this test exists to catch.
/// </summary>
public class PlatformSurfaceTests
{
    // "customer books an appointment" (samples/AppointmentTests/Scenarios.cs) has four authored
    // steps (Given patient exists, Given available slot, When create appointment, Then appointment
    // exists) plus the Teardown node every scenario reports, even with nothing registered.
    private const string FilteredScenario = "customer books an appointment";
    private const int FilteredScenarioStepCount = 5;

    // The sample's full step count across every scenario, confirmed by `--list-tests`.
    private const int UnfilteredTotalCount = 52;

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Help_lists_the_registered_platform_options()
    {
        var result = await RunSampleAsync("--help");

        Assert.True(
            result.ExitCode == 0,
            $"dotnet run -- --help exited {result.ExitCode}.{Environment.NewLine}{result.Describe()}");
        Assert.Contains("--treenode-filter", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("--maximum-failed-tests", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("--filter-uid", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tree_filter_selects_only_the_targeted_scenarios_steps()
    {
        var unfiltered = await RunSampleAsync();
        Assert.True(
            unfiltered.ExitCode == 0,
            $"unfiltered run exited {unfiltered.ExitCode}.{Environment.NewLine}{unfiltered.Describe()}");
        var unfilteredTotal = ReadSummaryTotal(unfiltered.StdOut);
        Assert.Equal(UnfilteredTotalCount, unfilteredTotal);

        var filtered = await RunSampleAsync("--treenode-filter", $"/*/*/*/{FilteredScenario}/*");
        Assert.True(
            filtered.ExitCode == 0,
            $"filtered run exited {filtered.ExitCode}.{Environment.NewLine}{filtered.Describe()}");
        var filteredTotal = ReadSummaryTotal(filtered.StdOut);

        Assert.Equal(FilteredScenarioStepCount, filteredTotal);
    }

    private static async Task<ProcessResult> RunSampleAsync(params string[] sampleArgs)
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "samples", "AppointmentTests", "AppointmentTests.csproj");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--");
        foreach (var arg in sampleArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdOut.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdErr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            Assert.Fail(
                $"dotnet run timed out after {ProcessTimeout}. Partial output:{Environment.NewLine}" +
                new ProcessResult(-1, stdOut.ToString(), stdErr.ToString()).Describe());
        }

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited on its own between the timeout firing and the kill attempt.
        }
    }

    private static int ReadSummaryTotal(string output)
    {
        // The platform's summary reads, indented, e.g. "  total: 52".
        var match = Regex.Match(output, @"total:\s*(\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(
            match.Success,
            $"could not find a 'total: N' summary line in the run output:{Environment.NewLine}{output}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Walks up from the test assembly's own location until it finds the repo root
    /// (the directory containing <c>Raun.slnx</c>), so the test works from any build output layout.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Raun.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find Raun.slnx by walking up from '{AppContext.BaseDirectory}'; " +
            "PlatformSurfaceTests needs the repo root to locate the AppointmentTests sample.");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public string Describe() =>
            $"exit code: {ExitCode}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{StdOut}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{StdErr}";
    }
}
