using System.Diagnostics;

namespace Raun.Coverage.Projects;

/// <summary>Starts child <c>dotnet</c> commands that resolve their own SDK.</summary>
internal static class DotnetProcess
{
    // MSBuildLocator sets these for the SDK registered in this process. A child dotnet picks its SDK
    // from global.json, which may be another one (a net10.0 process cannot host a newer SDK's
    // MSBuild); inheriting them mixes two SDKs, which fails the child on Linux.
    private static readonly string[] RegisteredSdkVariables = ["MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath"];

    public static ProcessStartInfo StartInfo(string workingDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var name in RegisteredSdkVariables) { start.Environment.Remove(name); }
        return start;
    }
}
