using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Reporting.Coverage;

namespace Raun.Coverage.Classification;

internal static class SyntaxBranchClassifier
{
    public static BranchCategory Classify(BranchClassificationContext context)
    {
        if (context.Descriptor.Form == BranchForm.Helper)
        {
            return BranchCategory.ArgumentCheck;
        }
        if (context.Descriptor.Form is BranchForm.Catch or BranchForm.ExceptionFilter)
        {
            return BranchCategory.ErrorHandling;
        }
        var node = context.SyntaxNode;
        // A compound predicate belongs to the guard it evaluates. Do not cross executable boundaries.
        if (context.Descriptor.Form == BranchForm.ShortCircuit)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                {
                    break;
                }
                if (ancestor is IfStatementSyntax conditional && conditional.Condition.Span.Contains(node.Span)
                    || ancestor is ConditionalExpressionSyntax expression && expression.Condition.Span.Contains(node.Span))
                {
                    node = ancestor;
                    break;
                }
            }
        }
        var throws = Throws(node).ToArray();
        if (throws.Length > 0)
        {
            var argumentException = context.SemanticModel.Compilation.GetTypeByMetadataName("System.ArgumentException");
            return throws.All(thrown => IsArgumentException(thrown, context.SemanticModel, argumentException))
                ? BranchCategory.ArgumentCheck : BranchCategory.ErrorHandling;
        }
        if (context.Descriptor.Form is BranchForm.ConditionalAccess or BranchForm.Coalesce)
        {
            return BranchCategory.NullPropagation;
        }
        if (context.SyntaxNode is IfStatementSyntax loggingGuard
            && loggingGuard.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(invocation =>
                context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                { Name: "IsEnabled", ContainingType: { } containingType }
                && containingType.ToDisplayString() == "Microsoft.Extensions.Logging.ILogger"))
        {
            return BranchCategory.Logging;
        }
        return BranchCategory.Logic;
    }

    private static bool IsArgumentException(SyntaxNode thrown, SemanticModel model, INamedTypeSymbol? argumentException)
    {
        var expression = thrown is ThrowStatementSyntax statement ? statement.Expression : ((ThrowExpressionSyntax)thrown).Expression;
        for (var type = expression is null ? null : model.GetTypeInfo(expression).Type as INamedTypeSymbol;
             type is not null; type = type.BaseType)
        {
            if (argumentException is not null && SymbolEqualityComparer.Default.Equals(type, argumentException))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<SyntaxNode> Throws(SyntaxNode node)
    {
        return node switch
        {
            IfStatementSyntax conditional => DirectThrows(conditional.Statement)
                .Concat(conditional.Else is null ? [] : DirectThrows(conditional.Else.Statement)),
            ConditionalExpressionSyntax expression => ThrowExpressions(expression.WhenTrue).Concat(ThrowExpressions(expression.WhenFalse)),
            BinaryExpressionSyntax expression => ThrowExpressions(expression.Right),
            SwitchExpressionSyntax expression => expression.Arms.SelectMany(arm => ThrowExpressions(arm.Expression)),
            SwitchStatementSyntax selection => selection.Sections.SelectMany(section => section.Statements.SelectMany(DirectThrows)),
            _ => [],
        };
    }

    private static IEnumerable<SyntaxNode> DirectThrows(StatementSyntax statement)
        => statement is BlockSyntax block ? block.Statements.OfType<ThrowStatementSyntax>()
            : statement is ThrowStatementSyntax thrown ? [thrown] : [];

    private static IEnumerable<SyntaxNode> ThrowExpressions(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parentheses)
        {
            expression = parentheses.Expression;
        }
        return expression is ThrowExpressionSyntax thrown ? [thrown] : [];
    }
}
