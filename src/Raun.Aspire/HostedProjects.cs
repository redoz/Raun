using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Raun;

/// <summary>
/// The .NET projects an Aspire application runs, read from the application's own model. Every
/// project resource carries <see cref="IProjectMetadata"/> whichever way the AppHost added it
/// (<c>AddDotnetProject</c> by path, or the older <c>AddProject&lt;T&gt;</c>), so this does not
/// depend on how the AppHost project is written or on what it references.
/// </summary>
internal static class HostedProjects
{
    /// <summary>Each resource that runs a project, with the path of its project file. A file-based
    /// app (a lone <c>.cs</c> file) is not a project and is left out.</summary>
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
