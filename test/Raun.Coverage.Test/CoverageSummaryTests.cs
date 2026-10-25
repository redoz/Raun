using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Reporting.Coverage;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverageSummaryTests
{
    [Fact]
    public void Every_branch_lands_in_exactly_one_outcome_counter()
    {
        var member = new MemberIdentity("Api", "Finance", "Calculate()");
        var statuses = new[]
        {
            EvidenceStatus.Complete, EvidenceStatus.Exercised, EvidenceStatus.Incomplete, EvidenceStatus.Untriggered,
            EvidenceStatus.NotReached, EvidenceStatus.Reached, EvidenceStatus.Evaluated, EvidenceStatus.Unknown, EvidenceStatus.Unmeasurable,
        };
        var category = Assert.Single(ClassifiedCoverageAnalyzer.Summarize(
            statuses.Select(status => new ClassifiedCoverageAnalyzer.AnalyzedBranch(member, BranchCategory.Logic, MeasurementKind.Decision, status))));
        Assert.Equal(2, category.Counts.Covered);
        Assert.Equal(2, category.Counts.Partial);
        Assert.Equal(1, category.Counts.Uncovered);
        Assert.Equal(4, category.Counts.Unknown);
        Assert.Equal(9, category.Counts.Inventory);
        Assert.Equal(category.Counts.Inventory, category.Counts.Covered + category.Counts.Partial + category.Counts.Uncovered + category.Counts.Unknown);
    }
}
