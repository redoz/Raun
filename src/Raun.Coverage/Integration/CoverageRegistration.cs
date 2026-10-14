using System.Runtime.CompilerServices;
using Microsoft.Testing.Platform.Builder;
using Raun.Coverage;

namespace Raun.Mtp.Coverage;

internal sealed class CoverageRegistration
{
    private static readonly ConditionalWeakTable<ITestApplicationBuilder, CoverageRegistration> Applications = new();
    public bool Enabled { get; set; }
    public List<Func<IBranchClassificationMiddleware>> Factories { get; } = [];
    public static CoverageRegistration For(ITestApplicationBuilder builder) => Applications.GetValue(builder, _ => new CoverageRegistration());
}
