using System.Diagnostics.CodeAnalysis;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;

namespace Raun.Mtp.Coverage;

// Inline consumption guarantees enrichment completes before session-finishing publication returns.
#pragma warning disable TPEXP // IBlockingDataConsumer is required to prevent analysis outliving host disposal.
internal sealed class CoverageArtifactConsumer(CoverageSessionCoordinator coordinator, bool enabled) : IDataConsumer, IBlockingDataConsumer
#pragma warning restore TPEXP
{
    public string Uid => "raun.mtp.classifiedcoverage";
    public string Version => "1.0.0";
    public string DisplayName => "Raun classified coverage";
    public string Description => "Enriches the HTML report with source-classified branch coverage.";
    public Task<bool> IsEnabledAsync() => Task.FromResult(enabled);
    [SuppressMessage("Design", "CA1819:Properties should not return arrays", Justification = "Required by MTP IDataConsumer.")]
    public Type[] DataTypesConsumed => [typeof(SessionFileArtifact)];

    public Task ConsumeAsync(IDataProducer dataProducer, IData data, CancellationToken cancellationToken)
    {
        if (data is SessionFileArtifact artifact && artifact.DisplayName == "Code Coverage"
            && dataProducer.Uid == "TestingPlatformCoverageDynamicTestSessionLifetimeHandler")
        {
            NodeDiagnostics.Log("coverage-artifact", dataProducer.Uid);
            return coordinator.AcceptArtifactAsync(artifact.SessionUid.Value, artifact.FileInfo, cancellationToken);
        }
        return Task.CompletedTask;
    }
}
