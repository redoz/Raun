using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Raun.Generator.Lowering;

namespace Raun.Generator.Analysis;

/// <summary>
/// Suppresses CA1822 ("mark members as static") on steps. A step is a public instance method of its
/// step class even when it reads neither <c>World</c> nor <c>Context</c>: a scenario calls it through a
/// phase property (<c>Given.PatientExists(…)</c>), which a static method would not compile against.
/// Making it static is never the fix, so the rule has nothing to say about it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StepMethodSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor MarkAsStatic = new(
        "RAUNSPR1822",
        "CA1822",
        "A step is an instance method of its step class: scenarios call it through a phase property, so it cannot be static.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = [MarkAsStatic];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Location.SourceTree is not { } tree)
            {
                continue;
            }

            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            var model = context.GetSemanticModel(tree);
            var symbol = node.AncestorsAndSelf()
                .Select(n => model.GetDeclaredSymbol(n, context.CancellationToken))
                .FirstOrDefault(s => s is not null);

            if (symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } method
                && SymbolHelpers.WorldOfStepClass(method.ContainingType) is not null)
            {
                context.ReportSuppression(Suppression.Create(MarkAsStatic, diagnostic));
            }
        }
    }
}
