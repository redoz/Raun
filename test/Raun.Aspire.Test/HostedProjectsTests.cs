using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Raun.Aspire.Test;

/// <summary>
/// Which source projects an Aspire application runs, read from its resource model: that is what
/// coverage is told to look at. Starting a real AppHost is the Aspire sample's job.
/// </summary>
public class HostedProjectsTests
{
    private sealed class Service(string name) : Resource(name);

    private sealed class ProjectAt(string path, bool fileBased = false) : IProjectMetadata
    {
        public string ProjectPath => path;
        public bool IsFileBasedApp => fileBased;
    }

    private static Service With(string name, params IResourceAnnotation[] annotations)
    {
        var resource = new Service(name);
        foreach (var annotation in annotations)
        {
            resource.Annotations.Add(annotation);
        }

        return resource;
    }

    [Fact]
    public void A_resource_that_runs_a_project_is_named_with_its_project_file()
    {
        var project = Path.Combine(Path.GetTempPath(), "Api", "Api.csproj");
        var model = new DistributedApplicationModel([With("api", new ProjectAt(project))]);

        Assert.Equal([("api", project)], HostedProjects.Of(model));
    }

    [Fact]
    public void A_resource_that_runs_no_project_is_not_named()
    {
        var model = new DistributedApplicationModel([With("cache")]);

        Assert.Empty(HostedProjects.Of(model));
    }

    [Fact]
    public void A_file_based_app_is_not_a_project()
    {
        var model = new DistributedApplicationModel([With("script", new ProjectAt(Path.Combine(Path.GetTempPath(), "app.cs"), fileBased: true))]);

        Assert.Empty(HostedProjects.Of(model));
    }

    [Fact]
    public void A_relative_project_path_is_made_absolute()
    {
        var model = new DistributedApplicationModel([With("api", new ProjectAt(Path.Combine("Api", "Api.csproj")))]);

        var (_, path) = Assert.Single(HostedProjects.Of(model));
        Assert.True(Path.IsPathFullyQualified(path));
    }
}
