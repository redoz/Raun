using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Raun.Test;

/// <summary>Exercises hard termination in a separate host, never in the test runner.</summary>
public class TimeoutProcessTests
{
    [Theory]
    [InlineData("scenario")]
    [InlineData("step")]
    [InlineData("synchronous")]
    [InlineData("preparation")]
    [InlineData("callback")]
    [InlineData("host")]
    public async Task Uncooperative_work_terminates_the_host_after_grace(string mode)
    {
        var result = await RunAsync(mode);
        Assert.Contains(mode == "preparation" ? "PREPARATION" : "BODY", result.Output, StringComparison.Ordinal);
        Assert.True(result.ExitCode != 0, $"Expected fatal exit after {mode} entered user code. Output: {result.Output}; Error: {result.Error}");
        Assert.Contains("Terminating the test host", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("EARLY_CLEANUP", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("COMPLETED", result.Output, StringComparison.Ordinal);
        if (mode != "preparation") Assert.Contains("CANCELED", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cooperative_work_drains_cleans_up_and_disarms_termination()
    {
        var result = await RunAsync("cooperative");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("RESULT:Failed", result.Output, StringComparison.Ordinal);
        Assert.Contains("SAFE_CLEANUP", result.Output, StringComparison.Ordinal);
        Assert.Contains("COMPLETED", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Hard_termination_bypasses_blocked_managed_shutdown()
    {
        var result = await RunAsync("process-exit");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Terminating the test host", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("PROCESS_EXIT_HANDLER", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Already_canceled_host_with_zero_grace_launches_no_body_and_does_not_terminate()
    {
        var result = await RunAsync("already-canceled");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("BODY", result.Output, StringComparison.Ordinal);
        Assert.Contains("RESULT:Skipped", result.Output, StringComparison.Ordinal);
        Assert.Contains("COMPLETED", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task A_deadline_before_invocation_prevents_entry_without_killing_the_host()
    {
        var result = await RunAsync("slow-startup");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("BODY", result.Output, StringComparison.Ordinal);
        Assert.Contains("RESULT:Failed", result.Output, StringComparison.Ordinal);
        Assert.Contains("COMPLETED", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string mode)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Raun.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = typeof(TimeoutProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "test", "Fixtures", "TimeoutHost", "bin", configuration, "net10.0", "TimeoutHost.dll"));
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
