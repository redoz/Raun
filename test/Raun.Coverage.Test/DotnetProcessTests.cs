using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class DotnetProcessTests
{
    [Fact]
    public void A_child_dotnet_does_not_inherit_the_registered_sdks_msbuild_paths()
    {
        // MSBuildLocator points these at the SDK it registered in this process. A child dotnet resolves
        // its own SDK from global.json, which can be a different one (a net10.0 process cannot host the
        // .NET 11 SDK's MSBuild), and on Linux the mismatch fails the child outright.
        var names = new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names) { Environment.SetEnvironmentVariable(name, "/registered-sdk"); }

            var start = DotnetProcess.StartInfo(Path.GetTempPath());

            Assert.Equal("dotnet", start.FileName);
            Assert.Equal(Path.GetTempPath(), start.WorkingDirectory);
            Assert.All(names, name => Assert.False(start.Environment.ContainsKey(name), name));
        }
        finally
        {
            foreach (var (name, value) in saved) { Environment.SetEnvironmentVariable(name, value); }
        }
    }
}
