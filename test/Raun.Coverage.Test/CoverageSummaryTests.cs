using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Reporting.Coverage;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CoverageSummaryTests
{
    [Fact]
    public void Unevaluated_filters_remain_separate_from_exact_decisions_and_expression_misses()
    {
        var member = new MemberIdentity("Api", "Finance", "Calculate()");
        var category = Assert.Single(ClassifiedCoverageAnalyzer.Summarize([
            new(member, BranchCategory.Logic, MeasurementKind.Decision, EvidenceStatus.Complete),
            new(member, BranchCategory.Logic, MeasurementKind.Filter, EvidenceStatus.NotReached),
            new(member, BranchCategory.Logic, MeasurementKind.Expression, EvidenceStatus.NotReached),
            new(member, BranchCategory.Logic, MeasurementKind.Filter, EvidenceStatus.Partial),
        ]));
        Assert.Equal(4, category.Counts.Inventory);
        Assert.Equal(1, category.Counts.Complete);
        Assert.Equal(1, category.Counts.DecisionMeasurable);
        Assert.Equal(2, category.Counts.NotReached);
        Assert.Equal(1, category.Counts.ExpressionNotReached);
        Assert.Equal(1, category.Counts.FilterNotReached);
        Assert.Equal(0, category.Counts.Partial);
        Assert.Equal(1, category.Counts.FilterPartial);
    }
}
