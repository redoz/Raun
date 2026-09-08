using System.Reflection;
using Microsoft.Testing.Platform.Extensions;

namespace Raun.Mtp;

/// <summary>
/// Raun's identity as the owner of the platform services it registers (the tree-node filter and the
/// maximum-failed-tests option). The platform shows it in <c>--info</c> and in error messages.
/// </summary>
internal sealed class RaunExtension : IExtension
{
    /// <summary>Stable uid; a literal on purpose, so renaming the class cannot silently change it.</summary>
    public string Uid => "raun.mtp";

    public string Version =>
        typeof(RaunExtension).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion is { Length: > 0 } informational
            ? informational.Split('+')[0]
            : "1.0.0";

    public string DisplayName => "Raun";

    public string Description => "Raun scenario test framework for Microsoft.Testing.Platform";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);
}
