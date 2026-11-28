using Microsoft.Testing.Platform.Builder;
using Xunit;

namespace Raun.Mtp.Test;

public class CoverageSourceProjectsTests
{
    [Fact]
    public void A_project_is_listed_once_by_its_full_path_in_the_order_it_was_named()
    {
        var projects = new CoverageSourceProjects();
        var api = Path.Combine(Path.GetTempPath(), "Api", "Api.csproj");
        var worker = Path.Combine(Path.GetTempPath(), "Worker", "Worker.csproj");

        projects.Add(api);
        projects.Add(worker);
        projects.Add(Path.Combine(Path.GetTempPath(), "Worker", "..", "Api", "Api.csproj"));

        Assert.Equal([api, worker], projects.Paths);
    }

    [Fact]
    public void A_relative_path_is_listed_as_an_absolute_one()
    {
        var projects = new CoverageSourceProjects();

        projects.Add(Path.Combine("Api", "Api.csproj"));

        Assert.True(Path.IsPathFullyQualified(Assert.Single(projects.Paths)));
    }

    [Fact]
    public void A_blank_path_is_refused()
        => Assert.ThrowsAny<ArgumentException>(() => new CoverageSourceProjects().Add(" "));

    [Fact]
    public async Task One_application_has_one_list_and_another_application_has_its_own()
    {
        var first = await TestApplication.CreateBuilderAsync([]);
        var second = await TestApplication.CreateBuilderAsync([]);

        first.GetCoverageSourceProjects().Add(Path.Combine(Path.GetTempPath(), "Api", "Api.csproj"));

        Assert.Single(first.GetCoverageSourceProjects().Paths);
        Assert.Empty(second.GetCoverageSourceProjects().Paths);
    }
}
