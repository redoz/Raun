using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Raun.Generator.Analysis;
using Raun.Generator.Emit;
using Raun.Generator.Lowering;

namespace Raun.Generator;

/// <summary>
/// Incremental generator that lowers every <c>[Raun.Scenario]</c> method into a manifest +
/// executor graph, emitted as a single <c>RaunScenarios.g.cs</c> registered with
/// <c>Raun.ScenarioRegistry</c> via a module initializer.
/// </summary>
[Generator]
public sealed class ScenarioGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var scenarios = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Raun.ScenarioAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static result => result is not null)
            .Collect();

        context.RegisterSourceOutput(scenarios, static (spc, items) =>
        {
            var parsed = new List<ParsedScenario>();
            foreach (var result in items)
            {
                if (result is not { } r)
                {
                    continue;
                }

                if (r.Error is not null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.UnhandledException, MakeLocation(r.File, r.Line), r.Error));
                }
                else if (r.Rejection is { } rejection)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.ScenarioNotGenerated, MakeLocation(rejection), rejection.ScenarioName));
                }
                else if (r.Scenario is not null)
                {
                    parsed.Add(r.Scenario);
                }
            }

            if (parsed.Count == 0)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(() => ScenarioEmitter.Emit(parsed));
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors.UnhandledException, Location.None, error));
                return;
            }

            spc.AddSource("RaunScenarios.g.cs", SourceText.From(source!, Encoding.UTF8));
        });

        // Entry point: emit a Main calling Raun.Mtp's bootstrap, gated on the MSBuild property
        // RaunGenerateProgram (default true). The default-true read keeps "just add the package"
        // working without any property set; setting it to false lets a consumer own Program.cs.
        // The generator ships in the Raun package, so it also runs for a project that references
        // Raun without Raun.Mtp (a scenario library, another host); there is no bootstrap to call,
        // and a Main naming one would be a compile error, so the emission also requires the
        // bootstrap type to be resolvable in the compilation.
        var wantsProgram = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ShouldGenerateProgram(provider));
        var hasBootstrap = context.CompilationProvider
            .Select(static (compilation, _) => compilation.GetTypeByMetadataName(EntryPointEmitter.BootstrapTypeName) is not null);
        var generateProgram = wantsProgram.Combine(hasBootstrap)
            .Select(static (pair, _) => pair.Left && pair.Right);

        context.RegisterSourceOutput(generateProgram, static (spc, generate) =>
        {
            if (!generate)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(EntryPointEmitter.Emit);
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors.UnhandledException, Location.None, error));
                return;
            }

            spc.AddSource(EntryPointEmitter.HintName, SourceText.From(source!, Encoding.UTF8));
        });
    }

    private static bool ShouldGenerateProgram(AnalyzerConfigOptionsProvider provider)
    {
        // Default true: emit unless the consumer explicitly opts out with RaunGenerateProgram=false.
        if (provider.GlobalOptions.TryGetValue("build_property.RaunGenerateProgram", out var value)
            && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return true;
    }

    private static ScenarioResult? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method || ctx.TargetNode is not MethodDeclarationSyntax syntax)
        {
            return null;
        }

        var lineSpan = syntax.Identifier.GetLocation().GetLineSpan();
        return GeneratorSafety.SafeParse(
            () => ScenarioParser.TryParse(ctx.SemanticModel, method, syntax),
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1);
    }

    /// <summary>The rejected statement's span, rebuilt as an external-file location (the pipeline
    /// carries values, not syntax).</summary>
    private static Location MakeLocation(ParseRejection rejection)
        => Location.Create(
            rejection.File,
            new TextSpan(rejection.SpanStart, rejection.SpanLength),
            new LinePositionSpan(
                new LinePosition(rejection.Lines.StartLine, rejection.Lines.StartChar),
                new LinePosition(rejection.Lines.EndLine, rejection.Lines.EndChar)));

    /// <summary>A 1-based file/line location for a diagnostic, or <see cref="Location.None"/> when the
    /// input had no path.</summary>
    private static Location MakeLocation(string? file, int line)
    {
        if (string.IsNullOrEmpty(file) || line <= 0)
        {
            return Location.None;
        }

        var position = new LinePosition(line - 1, 0);
        return Location.Create(file!, new TextSpan(0, 0), new LinePositionSpan(position, position));
    }
}
