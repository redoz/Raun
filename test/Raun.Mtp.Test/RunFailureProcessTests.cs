using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// A failure outside every step, seen from outside the process: what a real Microsoft.Testing.Platform
/// host prints and which exit code it returns. The fixture's first scenario resolves a scoped service
/// whose <c>Dispose</c> throws; its second scenario is healthy.
/// </summary>
public class RunFailureProcessTests
{
    [Fact]
    public async Task A_failure_outside_every_step_fails_the_real_host_and_names_the_scenario_and_the_cause()
    {
        var result = await RunAsync();

        Assert.Equal(2, result.ExitCode); // the platform's "at least one test failed"
        Assert.Contains("failed Raun run", result.Output, StringComparison.Ordinal);
        Assert.Contains("a scenario whose session cannot be closed", result.Output, StringComparison.Ordinal);
        Assert.Contains("the session could not be closed", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_other_scenarios_still_run_and_the_summary_is_printed()
    {
        var result = await RunAsync();

        // Two scenarios of setup, one step and teardown each, all passed, and the one failed run node.
        Assert.Contains("total: 7", result.Output, StringComparison.Ordinal);
        Assert.Contains("failed: 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("succeeded: 6", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenarios_generated_for_another_contract_fail_the_real_host_and_say_what_to_rebuild()
    {
        var result = await RunAsync(staleContract: true);

        var all = result.Output + result.Error;
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("generated for a different Raun", all, StringComparison.Ordinal);
        Assert.Contains("'RunFailureHost'", all, StringComparison.Ordinal);
        Assert.Contains("Rebuild", all, StringComparison.Ordinal);
        Assert.DoesNotContain("stale code ran", all, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(bool staleContract = false)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Raun.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = typeof(RunFailureProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "test", "Fixtures", "RunFailureHost", "bin", configuration, "net10.0", "RunFailureHost.dll"));
        start.ArgumentList.Add("--no-ansi");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(Path.Combine(Path.GetTempPath(), "raun-run-failure-host", Guid.NewGuid().ToString("N")));

        // The platform localizes its output; the assertions read the English text.
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["RAUN_FIXTURE_STALE_CONTRACT"] = staleContract ? "1" : "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
