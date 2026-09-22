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

    [Fact]
    public async Task A_platform_extension_added_through_configure_reaches_the_run()
    {
        // The generated entry point calls RunAsync(args) with no `configure`, so it registers no
        // extensions and rejects --report-trx as an unknown option. The supported route is a
        // hand-written Program.cs that passes `configure` — which is what the sample does, and what
        // README's "Reports and extensions" section documents. This proves that route end to end.
        var results = Path.Combine(Path.GetTempPath(), "raun-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(results);
        try
        {
            var result = await RunSampleAsync("--report-trx", "--results-directory", results);

            Assert.True(
                result.ExitCode == 0,
                $"--report-trx run exited {result.ExitCode}.{Environment.NewLine}{result.Describe()}");
            var trx = Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories);
            Assert.True(
                trx.Length == 1,
                $"expected exactly one .trx under '{results}', found {trx.Length}.{Environment.NewLine}{result.Describe()}");
            Assert.Contains("UnitTestResult", File.ReadAllText(trx[0]), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(results, recursive: true);
        }
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
        // ReadSummaryTotal matches the platform's English summary ("total: N"), but MTP localizes it
        // (German prints "gesamt:"). Pin English on the child. Both variables are needed: the platform
        // reads its own TESTINGPLATFORM_UI_LANGUAGE ahead of the SDK's DOTNET_CLI_UI_LANGUAGE, and a
        // test host that is itself an MTP app exports the former into this process's environment.
        startInfo.Environment["TESTINGPLATFORM_UI_LANGUAGE"] = "en";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
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
