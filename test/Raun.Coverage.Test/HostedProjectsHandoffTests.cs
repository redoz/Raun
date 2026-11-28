using Raun.Mtp.Coverage;
using Xunit;

namespace Raun.Coverage.Test;

[Collection(nameof(HostedProjectsHandoffTests))]
[CollectionDefinition(nameof(HostedProjectsHandoffTests), DisableParallelization = true)]
public sealed class HostedProjectsHandoffTests
{
    private static string NewFile() => Path.Combine(Path.GetTempPath(), "raun-handoff-test-" + Guid.NewGuid().ToString("N") + ".txt");

    private static readonly string Api = Path.Combine(Path.GetTempPath(), "Api", "Api.csproj");

    [Fact]
    public void What_the_host_leaves_the_controller_takes_once()
    {
        var file = NewFile();
        var worker = Path.Combine(Path.GetTempPath(), "Worker", "Worker.csproj");

        HostedProjectsHandoff.Leave(file, [Api, worker]);

        Assert.Equal([Api, worker], HostedProjectsHandoff.Take(file));
        Assert.False(File.Exists(file), "the hand-off file outlived its reader");
        Assert.Empty(HostedProjectsHandoff.Take(file));
    }

    [Fact]
    public void A_host_that_hosted_nothing_leaves_nothing()
    {
        var file = NewFile();

        HostedProjectsHandoff.Leave(file, []);

        Assert.False(File.Exists(file));
        Assert.Empty(HostedProjectsHandoff.Take(file));
    }

    [Fact]
    public void A_file_the_host_cannot_write_does_not_fail_the_run()
        => HostedProjectsHandoff.Leave(Path.Combine(Path.GetTempPath(), "raun-no-such-directory-" + Guid.NewGuid().ToString("N"), "list.txt"), [Api]);

    [Fact]
    public void A_host_learns_the_file_from_the_environment_its_controller_gave_it()
    {
        var before = Environment.GetEnvironmentVariable(HostedProjectsHandoff.Variable);
        try
        {
            Environment.SetEnvironmentVariable(HostedProjectsHandoff.Variable, null);
            Assert.Null(HostedProjectsHandoff.FileNamedByController);

            var file = NewFile();
            Environment.SetEnvironmentVariable(HostedProjectsHandoff.Variable, file);
            Assert.Equal(file, HostedProjectsHandoff.FileNamedByController);
        }
        finally { Environment.SetEnvironmentVariable(HostedProjectsHandoff.Variable, before); }
    }
}
