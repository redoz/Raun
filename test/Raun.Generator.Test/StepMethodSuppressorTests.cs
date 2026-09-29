using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Raun.Generator.Analysis;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>A step that reads neither World nor Context is still an instance method, so CA1822 ("mark
/// members as static") is suppressed on steps — and only on steps.</summary>
public class StepMethodSuppressorTests
{
    [Fact]
    public async Task CA1822_is_suppressed_on_steps_and_nowhere_else()
    {
        var compilation = GeneratorHarness.CompilationFor(
            StepClassTests.Clinic,
            new CSharpParseOptions(LanguageVersion.Preview));

        var diagnostics = await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new EveryInstanceMethodCouldBeStatic(), new StepMethodSuppressor()),
                new CompilationWithAnalyzersOptions(
                    new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true))
            .GetAnalyzerDiagnosticsAsync();

        IEnumerable<Diagnostic> On(string method) => diagnostics.Where(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture) == method);

        Assert.True(Assert.Single(On("Exists")).IsSuppressed);           // a step of a group
        Assert.True(Assert.Single(On("CalendarIsFree")).IsSuppressed);   // a step of a custom phase
        Assert.NotEmpty(On("DisposeAsync"));                             // the world and the isolation are not step classes
        Assert.All(On("DisposeAsync"), d => Assert.False(d.IsSuppressed));
    }

    /// <summary>A stand-in for CA1822 that flags every public instance method by name.</summary>
    // A test double that never ships: the analyzer-authoring rules (target framework, release tracking,
    // reserved ids) are about analyzers that do, and reusing CA1822's id is the point.
#pragma warning disable RS1041, RS1036, RS1029, RS2008
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class EveryInstanceMethodCouldBeStatic : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "CA1822", "Mark members as static", "{0}", "Performance", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(
                symbolContext =>
                {
                    if (symbolContext.Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } method
                        && method.Locations.FirstOrDefault() is { IsInSource: true } location)
                    {
                        symbolContext.ReportDiagnostic(Diagnostic.Create(Rule, location, method.Name));
                    }
                },
                SymbolKind.Method);
        }
    }
#pragma warning restore RS1041, RS1036, RS1029, RS2008
}
