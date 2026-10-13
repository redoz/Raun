using Microsoft.Testing.Platform.Builder;
using Raun.Coverage;
using Raun.Coverage.Projects;
using Raun.Mtp;
using Raun.Reporting.Coverage;

var builder = await TestApplication.CreateBuilderAsync([]);
builder.UseBranchClassificationMiddleware(() => new ProjectRule());
ClassifiedCoverageAnalyzer.Prepare(args[1]);
var report = await ClassifiedCoverageAnalyzer.AnalyzeAsync(args[0], args[1], new CollectorScope(), [() => new ProjectRule()]);
if (report.Status != "available" || !report.Categories.Any(c => c.Category == BranchCategory.Logging && c.Counts.Inventory > 0))
    throw new InvalidOperationException("Packaged public middleware did not classify the source inventory.");
Console.WriteLine($"Package-only workspace and custom classifier passed: {report.Types.Count} types.");

sealed class ProjectRule : IBranchClassificationMiddleware
{
    public BranchCategory Classify(BranchClassificationContext context, BranchClassificationNext next)
    {
        var category = next();
        return category == BranchCategory.Logic ? BranchCategory.Logging : category;
    }
}
