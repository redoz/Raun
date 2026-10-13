using Microsoft.Build.Locator;

namespace Raun.Coverage.Projects;

internal static class WorkspaceSdk
{
    private static readonly object Gate = new();
    private static Version? _version;

    public static Version EnsureRegistered(string workingDirectory)
    {
        lock (Gate)
        {
            if (_version is not null)
            {
                return _version;
            }
            var instances = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = workingDirectory,
            }).ToArray();
            var instance = instances.FirstOrDefault()
                ?? throw new InvalidOperationException("No compatible .NET SDK was found for the source solution.");
            MSBuildLocator.RegisterInstance(instance);
            _version = instance.Version;
            return _version;
        }
    }
}
