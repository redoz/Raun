using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Coverage.Projects;

namespace Raun.Coverage.Inventory;

internal enum MeasurementKind { Guard, Handler, Decision, Expression, Filter, Helper }

internal sealed record InventoryBranch(
    BranchDescriptor Descriptor,
    SyntaxNode Syntax,
    SemanticModel SemanticModel,
    MeasurementKind Measurement,
    IReadOnlyList<FileLinePositionSpan?> Outcomes)
{
    public BranchClassificationContext Context => new(Descriptor, Syntax, SemanticModel);
}

internal static class BranchInventoryBuilder
{
    public static IReadOnlyList<InventoryBranch> Build(Compilation compilation, CollectorScope scope, CancellationToken cancellationToken)
    {
        var branches = new List<InventoryBranch>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!scope.IncludesSource(tree.FilePath))
            {
                continue;
            }
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Describe(node, model) is not { } description)
                {
                    continue;
                }
                var executingSymbol = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);
                if (executingSymbol is not null && scope.IsExcluded(executingSymbol)) { continue; }
                var owner = FindOwner(node, model, cancellationToken);
                if (owner is not null && (scope.IsExcluded(owner) || !scope.IncludesFunction(owner.ToDisplayString())))
                {
                    continue;
                }
                var member = new MemberIdentity(compilation.AssemblyName ?? "",
                    owner?.ContainingType?.ToDisplayString() ?? "Program",
                    owner?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "<top-level>") { Namespace = owner?.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty };
                if (!scope.IncludesCoverletType(member.Assembly, member.Type)) { continue; }
                var id = string.Join("|", member.Assembly, member.Type, member.Signature, tree.FilePath,
                    node.SpanStart.ToString(CultureInfo.InvariantCulture), node.RawKind.ToString(CultureInfo.InvariantCulture));
                branches.Add(new InventoryBranch(new BranchDescriptor(id, description.Form, node.GetLocation().GetLineSpan(), member),
                    node, model, description.Measurement, description.Outcomes));
            }
        }
        return branches;
    }

    private static ISymbol? FindOwner(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        var owner = node.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax
            or PropertyDeclarationSyntax || (n is VariableDeclaratorSyntax && n.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax));
        if (owner is not null)
        {
            return model.GetDeclaredSymbol(owner, cancellationToken);
        }
        var symbol = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }
        return symbol;
    }

    internal static (BranchForm Form, MeasurementKind Measurement, IReadOnlyList<FileLinePositionSpan?> Outcomes)? Describe(
        SyntaxNode node, SemanticModel model)
    {
        switch (node)
        {
            case IfStatementSyntax conditional:
                if ((DirectThrow(conditional.Statement) ?? (conditional.Else is null ? null : DirectThrow(conditional.Else.Statement))) is { } thrown)
                {
                    return (BranchForm.If, MeasurementKind.Guard, [Span(thrown)]);
                }
                return (BranchForm.If, MeasurementKind.Decision,
                    [EntrySpan(conditional.Statement), conditional.Else is null ? ImplicitElse(conditional, model) : EntrySpan(conditional.Else.Statement)]);
            case SwitchStatementSyntax selection:
                var outcomes = selection.Sections.Select(s => EntrySpan(s.Statements.FirstOrDefault())).ToList();
                if (!selection.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax)))
                {
                    outcomes.Add(null);
                }
                return (BranchForm.Switch, MeasurementKind.Decision, outcomes);
            case CatchClauseSyntax handler:
                return (BranchForm.Catch, MeasurementKind.Handler, [EntrySpan(handler.Block)]);
            case CatchFilterClauseSyntax filter:
                return (BranchForm.ExceptionFilter, MeasurementKind.Filter, [Span(filter.FilterExpression)]);
            case ConditionalExpressionSyntax:
                return (BranchForm.Conditional, MeasurementKind.Expression, [Span(node)]);
            case ConditionalAccessExpressionSyntax:
                return (BranchForm.ConditionalAccess, MeasurementKind.Expression, [Span(node)]);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression):
                return (BranchForm.Coalesce, MeasurementKind.Expression, [Span(node)]);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression):
                return (BranchForm.ShortCircuit, MeasurementKind.Expression, [Span(node)]);
            case SwitchExpressionSyntax:
                return (BranchForm.SwitchExpression, MeasurementKind.Expression, [Span(node)]);
            case InvocationExpressionSyntax invocation when IsHelper(invocation, model):
                return (BranchForm.Helper, MeasurementKind.Helper, []);
            default:
                return null;
        }
    }

    private static bool IsHelper(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method || !method.Name.StartsWith("ThrowIf", StringComparison.Ordinal))
        {
            return false;
        }
        for (var type = method.ContainingType; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == "System.ArgumentException")
            {
                return true;
            }
        }
        return false;
    }

    private static FileLinePositionSpan? ImplicitElse(IfStatementSyntax conditional, SemanticModel model)
    {
        if (conditional.Parent is not BlockSyntax block || model.AnalyzeControlFlow(conditional.Statement) is not { Succeeded: true, EndPointIsReachable: false })
        {
            return null;
        }
        // A jump can enter the continuation without traversing this decision.
        if (block.DescendantNodes().Any(node => node is GotoStatementSyntax or LabeledStatementSyntax)) { return null; }
        return EntrySpan(block.Statements.Skip(block.Statements.IndexOf(conditional) + 1)
            .FirstOrDefault(statement => statement is not LocalFunctionStatementSyntax and not EmptyStatementSyntax));
    }

    private static FileLinePositionSpan? EntrySpan(StatementSyntax? statement)
        => statement switch
        {
            BlockSyntax block => FirstStatement(block) is { } first ? EntrySpan(first) : block.OpenBraceToken.GetLocation().GetLineSpan(),
            IfStatementSyntax conditional => Span(conditional.Condition),
            SwitchStatementSyntax selection => Span(selection.Expression),
            _ => Span(statement),
        };
    private static ThrowStatementSyntax? DirectThrow(StatementSyntax statement)
    {
        return statement is BlockSyntax block ? block.Statements.OfType<ThrowStatementSyntax>().FirstOrDefault()
            : statement as ThrowStatementSyntax;
    }

    private static StatementSyntax? FirstStatement(StatementSyntax statement)
        => statement is BlockSyntax block ? block.Statements.FirstOrDefault(s => s is not LocalFunctionStatementSyntax and not EmptyStatementSyntax) : statement;

    private static FileLinePositionSpan? Span(SyntaxNode? node) => node?.GetLocation().GetLineSpan();
}
