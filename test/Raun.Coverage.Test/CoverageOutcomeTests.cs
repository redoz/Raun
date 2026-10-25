using Raun.Coverage.MicrosoftXml;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverageOutcomeTests
{
    [Theory]
    [InlineData((int)EvidenceStatus.Complete, "covered", null)]
    [InlineData((int)EvidenceStatus.Exercised, "covered", null)]
    [InlineData((int)EvidenceStatus.Incomplete, "partial", null)]
    [InlineData((int)EvidenceStatus.Partial, "partial", null)]
    [InlineData((int)EvidenceStatus.Untriggered, "partial", "untriggered")]
    [InlineData((int)EvidenceStatus.NotReached, "uncovered", null)]
    [InlineData((int)EvidenceStatus.Reached, "unknown", "expression")]
    [InlineData((int)EvidenceStatus.Evaluated, "unknown", "filter")]
    [InlineData((int)EvidenceStatus.SharedRange, "unknown", "shared-range")]
    [InlineData((int)EvidenceStatus.Unknown, "unknown", "unresolved")]
    [InlineData((int)EvidenceStatus.Unmeasurable, "unknown", "library")]
    public void Every_status_maps_to_one_outcome(int statusValue, string outcome, string? reason)
        => Assert.Equal((outcome, reason), CoverageOutcome.Of((EvidenceStatus)statusValue));

    [Fact]
    public void The_mapping_covers_the_whole_enum()
        => Assert.All(Enum.GetValues<EvidenceStatus>(), status => Assert.Contains(CoverageOutcome.Of(status).Outcome,
            new[] { "covered", "partial", "uncovered", "unknown" }));
}
