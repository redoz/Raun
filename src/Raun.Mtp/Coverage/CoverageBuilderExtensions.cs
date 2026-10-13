using Microsoft.Testing.Platform.Builder;
using Raun.Coverage;
using Raun.Mtp.Coverage;

namespace Raun.Mtp;

/// <summary>Test-project customization of classified coverage.</summary>
public static class CoverageBuilderExtensions
{
    /// <summary>Adds an application-scoped rule. First registration runs first; next delegates to defaults.</summary>
    public static ITestApplicationBuilder UseBranchClassificationMiddleware(
        this ITestApplicationBuilder builder, Func<IBranchClassificationMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        CoverageRegistration.For(builder).Factories.Add(factory);
        return builder;
    }
}
