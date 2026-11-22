using Raun.Mtp.Coverage;
using Xunit;

namespace Raun.Coverage.Test;

/// <summary>
/// The list of hosted source projects crossing from the test host, where the run learns it, to the
/// controller, where Coverlet's report is analysed after the host has exited.
/// </summary>
[Collection(nameof(HostedProjectsHandoffTests))]
[CollectionDefinition(nameof(HostedProjectsHandoffTests), DisableParallelization = true)]
public sealed class HostedProjectsHandoffTests
{
    private static string NewFile() => Path.Combine(Path.GetTempPath(), "raun-handoff-test-" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>Runs <paramref name="host"/> as the test host would: with the controller's file in
    /// its environment.</summary>
    private static void AsHost(string? file, Action host)
    {
        var before = Environment.GetEnvironmentVariable(HostedProjectsHandoff.Variable);
        Environment.SetEnvironmentVariable(HostedProjectsHandoff.Variable, file);
        try { host(); }
        finally { Environment.SetEnvironmentVariable(HostedProjectsHandoff.Variable, before); }
    }

    [Fact]
    public void What_the_host_leaves_the_controller_takes_once()
    {
        var file = NewFile();
        var api = Path.Combine(Path.GetTempPath(), "Api", "Api.csproj");
        var worker = Path.Combine(Path.GetTempPath(), "Worker", "Worker.csproj");

        AsHost(file, () => HostedProjectsHandoff.Leave([api, worker]));

        Assert.Equal([api, worker], HostedProjectsHandoff.Take(file));
        Assert.False(File.Exists(file), "the hand-off file outlived its reader");
        Assert.Empty(HostedProjectsHandoff.Take(file));
    }

    [Fact]
    public void A_host_that_hosted_nothing_leaves_nothing()
    {
        var file = NewFile();

        AsHost(file, () => HostedProjectsHandoff.Leave([]));

        Assert.False(File.Exists(file));
        Assert.Empty(HostedProjectsHandoff.Take(file));
    }

    [Fact]
    public void A_host_nobody_asked_leaves_nothing()
        => AsHost(null, () => HostedProjectsHandoff.Leave([Path.Combine(Path.GetTempPath(), "Api", "Api.csproj")]));

    [Fact]
    public void A_file_the_host_cannot_write_does_not_fail_the_run()
        => AsHost(Path.Combine(Path.GetTempPath(), "raun-no-such-directory-" + Guid.NewGuid().ToString("N"), "list.txt"),
            () => HostedProjectsHandoff.Leave([Path.Combine(Path.GetTempPath(), "Api", "Api.csproj")]));
}
