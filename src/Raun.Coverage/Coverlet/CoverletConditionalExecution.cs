using System.Reflection.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;

namespace Raun.Coverage.Coverlet;

internal static class CoverletConditionalExecution
{
    public static CoverageRange[] Build(IEnumerable<InventoryBranch> branches, IReadOnlyList<CoverageModule> modules)
    {
        var result = new List<CoverageRange>();
        foreach (var branch in branches.Where(branch => branch.Syntax is ConditionalExpressionSyntax))
        {
            var expression = (ConditionalExpressionSyntax)branch.Syntax;
            // Optimized compound conditions may have no distinct IL branch. Limit exact
            // coloring to simple, nonconstant conditions until lowering is fully proven.
            if (branch.SemanticModel.GetConstantValue(expression.Condition).HasValue
                || expression.Condition.DescendantNodesAndSelf().Any(node => node is ConditionalExpressionSyntax or ConditionalAccessExpressionSyntax
                    || node.RawKind is (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.TrueLiteralExpression or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.FalseLiteralExpression
                    || node is BinaryExpressionSyntax binary && binary.RawKind is (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalAndExpression or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalOrExpression or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.CoalesceExpression)) { continue; }
            var trueCalls = Calls(expression.WhenTrue, branch.SemanticModel);
            var falseCalls = Calls(expression.WhenFalse, branch.SemanticModel);
            if (trueCalls.Count == 0 || falseCalls.Count == 0 || trueCalls.Overlaps(falseCalls)) { continue; }
            var span = expression.GetLocation().GetLineSpan();
            var candidates = new List<(CoverageSource Source, CoveragePath True, CoveragePath False)>();
            foreach (var module in modules.Where(module => module.IsCoverlet))
            {
                foreach (var method in module.Methods)
                {
                    var point = method.Points.FirstOrDefault(point => SamePath(point.Source.Path, span.Path)
                        && point.Start.CompareTo(span.StartLinePosition) <= 0 && point.End.CompareTo(span.EndLinePosition) >= 0
                        && CoverageResolver.VerifySource(branch, point.Source));
                    if (point is null) { continue; }
                    foreach (var instruction in method.Instructions.Where(instruction => instruction.IsConditional && instruction.Target >= 0
                        && instruction.Offset >= point.ILStart && instruction.Offset < point.ILEnd))
                    {
                        var left = Reachable(method, instruction.NextOffset);
                        var right = Reachable(method, instruction.Target);
                        if (left is null || right is null) { continue; }
                        var common = left.Intersect(right).ToHashSet();
                        var leftCalls = method.Instructions.Where(item => left.Contains(item.Offset) && !common.Contains(item.Offset) && item.Call is not null).Select(item => item.Call!).ToHashSet(StringComparer.Ordinal);
                        var rightCalls = method.Instructions.Where(item => right.Contains(item.Offset) && !common.Contains(item.Offset) && item.Call is not null).Select(item => item.Call!).ToHashSet(StringComparer.Ordinal);
                        var leftTrue = leftCalls.SetEquals(trueCalls) && rightCalls.SetEquals(falseCalls);
                        var rightTrue = rightCalls.SetEquals(trueCalls) && leftCalls.SetEquals(falseCalls);
                        if (leftTrue == rightTrue) { continue; }
                        var paths = module.Paths.Where(path => path.Offset == instruction.Offset && path.Line == point.Start.Line + 1 && SamePath(path.SourcePath, span.Path)).ToArray();
                        if (paths.Length != 2 || paths.Select(path => path.EndOffset).Distinct().Count() != 2) { continue; }
                        var leftPath = paths.SingleOrDefault(path => path.EndOffset == instruction.NextOffset);
                        var rightPath = paths.SingleOrDefault(path => path.EndOffset == instruction.Target);
                        if (leftPath is null || rightPath is null) { continue; }
                        // Coverlet folds lambda ownership. Identical source-line/IL branch
                        // signatures in multiple physical methods cannot identify an arm.
                        var owners = module.Methods.Count(owner => owner.Instructions.Any(item => item.Offset == instruction.Offset
                            && item.NextOffset == instruction.NextOffset && item.Target == instruction.Target && item.IsConditional)
                            && owner.Points.Any(sourcePoint => SamePath(sourcePoint.Source.Path, span.Path)
                                && sourcePoint.Start.Line == point.Start.Line && instruction.Offset >= sourcePoint.ILStart && instruction.Offset < sourcePoint.ILEnd));
                        if (owners != 1) { continue; }
                        candidates.Add((point.Source, leftTrue ? leftPath : rightPath, leftTrue ? rightPath : leftPath));
                    }
                }
            }
            if (candidates.Count != 1) { continue; }
            var candidate = candidates[0];
            if (candidate.True.Hits > 0 || candidate.False.Hits > 0) { Add(expression.Condition, CoverageHit.Yes); }
            Add(expression.WhenTrue, candidate.True.Hits > 0 ? CoverageHit.Yes : CoverageHit.No);
            Add(expression.WhenFalse, candidate.False.Hits > 0 ? CoverageHit.Yes : CoverageHit.No);
            void Add(SyntaxNode node, CoverageHit hit)
            {
                var location = node.GetLocation().GetLineSpan();
                var compound = node.DescendantNodesAndSelf().Any(child => child is ConditionalExpressionSyntax or ConditionalAccessExpressionSyntax || child is BinaryExpressionSyntax binary
                    && binary.RawKind is (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalAndExpression or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalOrExpression or (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.CoalesceExpression);
                result.Add(new CoverageRange(candidate.Source, location.StartLinePosition, location.EndLinePosition, hit == CoverageHit.Yes && compound ? CoverageHit.Partial : hit));
            }
        }
        return result.Distinct().ToArray();
    }

    private static HashSet<int>? Reachable(CoverageMethod method, int start)
    {
        var instructions = method.Instructions.ToDictionary(instruction => instruction.Offset);
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(start);
        while (pending.TryPop(out var offset))
        {
            if (!visited.Add(offset)) { continue; }
            if (!instructions.TryGetValue(offset, out var instruction)) { return null; }
            if (instruction.Code.FlowControl is FlowControl.Return or FlowControl.Throw) { continue; }
            if (instruction.Code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
            {
                if (instruction.Target < 0 || instruction.Target <= instruction.Offset) { return null; }
                pending.Push(instruction.Target);
                if (!instruction.IsConditional) { continue; }
            }
            pending.Push(instruction.NextOffset);
        }
        return visited;
    }

    private static HashSet<string> Calls(SyntaxNode node, SemanticModel model)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (node.DescendantNodesAndSelf().Any(child => child is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or AnonymousFunctionExpressionSyntax)) { return result; }
        foreach (var invocation in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) { return []; }
            method = (method.ReducedFrom ?? method).OriginalDefinition;
            static string TypeName(INamedTypeSymbol type) => type.ContainingType is { } parent ? TypeName(parent) + "+" + type.MetadataName
                : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;
            result.Add(TypeName(method.ContainingType) + "::" + method.MetadataName + "/" + method.Parameters.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
