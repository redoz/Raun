using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Raun;

/// <summary>Aspire puts <see cref="IProjectMetadata"/> on every resource that runs a project, whichever
/// way the AppHost added it (<c>AddDotnetProject</c> by path, or the older <c>AddProject&lt;T&gt;</c>).</summary>
internal static class HostedProjects
{
    public static IEnumerable<(string Resource, string ProjectPath)> Of(DistributedApplicationModel model)
    {
        foreach (var resource in model.Resources)
        {
            if (resource.Annotations.OfType<IProjectMetadata>().LastOrDefault() is { IsFileBasedApp: false, ProjectPath: { Length: > 0 } path })
            {
                yield return (resource.Name, Path.GetFullPath(path));
            }
        }
    }
}
