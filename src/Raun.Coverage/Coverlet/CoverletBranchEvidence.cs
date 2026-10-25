using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;

namespace Raun.Coverage.Coverlet;

internal static class CoverletBranchEvidence
{
    public static long[] PathHits(InventoryBranch branch, IReadOnlyList<CoverageModule> modules)
    {
        if (!modules.Any(module => module.IsCoverlet) || branch.Measurement is MeasurementKind.Handler or MeasurementKind.Filter or MeasurementKind.Helper) { return []; }
        var line = branch.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line;
        // Two source decisions on the same line cannot be paired with IL offset groups
        // without a proven lowered-method mapping. Never assign by JSON order or path number.
        var onLine = branch.Syntax.SyntaxTree.GetRoot().DescendantNodes().Where(node =>
            node.GetLocation().GetLineSpan().StartLinePosition.Line == line).Count(IsBranchNode);
        if (onLine != 1) { return []; }
        var groups = modules.Where(module => module.IsCoverlet).SelectMany(module => module.Paths)
            .Where(path => path.Line == line + 1 && string.Equals(Path.GetFullPath(path.SourcePath), Path.GetFullPath(branch.Syntax.SyntaxTree.FilePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .GroupBy(path => (path.Method, path.Offset)).ToArray();
        if (groups.Length != 1 || groups[0].Select(path => path.Path).Distinct().Count() != groups[0].Count()) { return []; }
        return groups[0].OrderBy(path => path.Path).Select(path => path.Hits).ToArray();
    }

    internal static bool IsBranchNode(SyntaxNode node)
        => node is IfStatementSyntax or SwitchStatementSyntax or ConditionalExpressionSyntax or SwitchExpressionSyntax
            or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax or CatchFilterClauseSyntax or SwitchExpressionArmSyntax
            or ConditionalAccessExpressionSyntax || node is BinaryExpressionSyntax binary &&
            (binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.CoalesceExpression)
                || binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalAndExpression)
                || binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalOrExpression));
}
