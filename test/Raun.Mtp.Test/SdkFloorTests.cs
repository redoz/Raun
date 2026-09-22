using System.Diagnostics;
using System.Text;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The SDK floor a package consumer hits. The generator is packed under
/// <c>analyzers/dotnet/roslyn5.3/cs</c> and the DSL needs C# 14, so a compiler older than the .NET 10
/// SDK 10.0.300 loads no generator, emits no diagnostics, and produces a project that builds green
/// with zero scenarios — the worst possible failure mode. <c>buildTransitive/Raun.props</c> turns that
/// into a build error, and these tests drive that file the way a consumer's build would: MSBuild
/// evaluates the shipped props and runs the check target, with the SDK version supplied as a property.
/// </summary>
public class SdkFloorTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData("9.0.100")]
    [InlineData("10.0.100")]
    [InlineData("10.0.203")]
    public async Task An_sdk_below_the_floor_fails_the_build(string sdkVersion)
    {
        var result = await CheckAsync(sdkVersion);

        Assert.True(result.ExitCode != 0, $"SDK {sdkVersion} was accepted.{Environment.NewLine}{result.Output}");
        Assert.Contains("RAUN018", result.Output, StringComparison.Ordinal);
        Assert.Contains("10.0.300", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.300")]
    [InlineData("10.0.301")]
    [InlineData("11.0.100")]
    public async Task An_sdk_at_or_above_the_floor_builds(string sdkVersion)
    {
        var result = await CheckAsync(sdkVersion);

        Assert.True(result.ExitCode == 0, $"SDK {sdkVersion} was rejected.{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public async Task An_unknown_sdk_version_is_not_blocked()
    {
        // Something other than `dotnet build` is driving MSBuild. Guessing wrong here would break a
        // build for no reason, so an unknown version is let through.
        var result = await CheckAsync(sdkVersion: "");

        Assert.True(result.ExitCode == 0, $"an unknown SDK version was rejected.{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public async Task The_check_can_be_switched_off()
    {
        var result = await CheckAsync("9.0.100", "-p:RaunSkipSdkCheck=true");

        Assert.True(result.ExitCode == 0, $"RaunSkipSdkCheck did not suppress the check.{Environment.NewLine}{result.Output}");
    }

    /// <summary>Evaluates the shipped props in a throwaway project outside the repository (so the
    /// repo's own Directory.Build.props cannot join in) and runs the check target on it.</summary>
    private static async Task<(int ExitCode, string Output)> CheckAsync(string sdkVersion, params string[] extraArgs)
    {
        var propsPath = Path.Combine(FindRepoRoot(), "src", "Raun", "buildTransitive", "Raun.props");
        Assert.True(File.Exists(propsPath), $"the consumer props file is missing at '{propsPath}'.");

        var dir = Path.Combine(Path.GetTempPath(), "raun-sdkfloor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var project = Path.Combine(dir, "consumer.proj");
            await File.WriteAllTextAsync(
                project,
                $"""
                <Project>
                  <Import Project="{propsPath}" />
                </Project>
                """);

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(project);
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-t:RaunCheckSdkFloor");
            startInfo.ArgumentList.Add($"-p:NETCoreSdkVersion={sdkVersion}");
            foreach (var arg in extraArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(ProcessTimeout);
            await process.WaitForExitAsync(cts.Token);

            lock (output)
            {
                return (process.ExitCode, output.ToString());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

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

        throw new InvalidOperationException($"Could not find Raun.slnx by walking up from '{AppContext.BaseDirectory}'.");
    }
}
