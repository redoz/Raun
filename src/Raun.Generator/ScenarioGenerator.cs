using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Raun.Generator.Diagnostics;
using Raun.Generator.Emit;
using Raun.Generator.Analysis;
using Raun.Generator.Lowering;

namespace Raun.Generator;

/// <summary>
/// Incremental generator that lowers every <c>[Raun.Scenario]</c> method into a manifest +
/// executor graph. Each scenario is emitted into its own file, and one <c>RaunScenarios.g.cs</c>
/// registers them all with <c>Raun.ScenarioRegistry</c> through a module initializer; every file is
/// a part of the same <c>Raun.Generated.RaunGenerated</c> class.
/// </summary>
[Generator]
public sealed class ScenarioGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mappings = context.CompilationProvider.Select(static (compilation, _) =>
            (Data: ResourceMappingDiscovery.Discover(compilation), Assembly: compilation.Assembly.Identity.ToString()));
        var mappingDiagnostics = mappings.SelectMany(static (mapping, _) => mapping.Data.Diagnostics);
        context.RegisterSourceOutput(mappingDiagnostics, static (spc, diagnostic) => spc.ReportDiagnostic(diagnostic));
        var mappingSource = mappings.SelectMany(static (mapping, _) => mapping.Data.HasMappings
            ? new[] { ResourceMappingEmitter.Emit(mapping.Data, mapping.Assembly) } : System.Array.Empty<string>());
        context.RegisterSourceOutput(mappingSource, static (spc, source) =>
            spc.AddSource("RaunResourceMappings.g.cs", SourceText.From(source, Encoding.UTF8)));
        var hasMappings = mappings.Select(static (mapping, _) => mapping.Data.HasMappings);

        var scenarios = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Raun.ScenarioAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx))
            .SelectMany(static (results, _) => results)
            .Where(static result => result is not null);

        // One output per scenario: an edit re-emits that scenario's file and leaves its neighbours
        // alone. Its diagnostics are reported here too, where the scenario they name is.
        context.RegisterSourceOutput(scenarios.Combine(hasMappings), static (spc, pair) =>
        {
            var result = pair.Left;
            var hasResourceMappings = pair.Right;

            if (result is not { } r)
            {
                return;
            }

            if (r.Error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.UnhandledException, MakeLocation(r.File, r.Line), r.Error));
                return;
            }

            if (r.Scenario is not { } scenario)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(() => ScenarioEmitter.EmitScenario(scenario, hasResourceMappings));
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors.UnhandledException, Location.None, error));
                return;
            }

            spc.AddSource(
                ScenarioEmitter.ScenarioHintPrefix + scenario.SafeName + ".g.cs",
                SourceText.From(source!, Encoding.UTF8));
        });

        // A scenario's diagnostics, located in its source tree so the editor can squiggle them and
        // #pragma/[SuppressMessage] apply. Finding the tree needs the compilation, which changes on
        // every edit; joining it here, apart from emission, keeps the emitted files cached while the
        // cheap reporting re-runs.
        var diagnostics = scenarios
            .Select(static (result, _) => result?.Diagnostics ?? EquatableArray<ScenarioDiagnostic>.Empty)
            .Where(static diagnostics => diagnostics.Count > 0)
            .Combine(context.CompilationProvider);

        context.RegisterSourceOutput(diagnostics, static (spc, pair) =>
        {
            var (found, compilation) = pair;
            foreach (var diagnostic in found)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.ById[diagnostic.Id], MakeLocation(diagnostic, compilation), [.. diagnostic.Arguments]));
            }
        });

        // The registry: the module initializer and CreateAll, over the scenario NAMES only, so it
        // stands still while a step body is edited and moves only when a scenario is added, removed
        // or renamed.
        var registry = scenarios
            .Select(static (result, _) => result?.Scenario is { } s
                ? new RegistryEntry(s.SafeName, s.MethodFullName, s.RegistrationKey)
                : default)
            .Where(static entry => entry.SafeName is not null)
            .Collect();

        context.RegisterSourceOutput(registry, static (spc, entries) =>
        {
            if (entries.Length == 0)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(() => ScenarioEmitter.EmitRegistry(entries));
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors.UnhandledException, Location.None, error));
                return;
            }

            spc.AddSource(ScenarioEmitter.RegistryHintName, SourceText.From(source!, Encoding.UTF8));
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

        var hasClassifiedCoverage = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName("Raun.Mtp.CoverageBuilderExtensions")?.GetMembers("UseClassifiedCoverage").Length > 0);
        context.RegisterSourceOutput(generateProgram.Combine(hasClassifiedCoverage), static (spc, options) =>
        {
            if (!options.Left)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(() => EntryPointEmitter.Emit(options.Right));
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

    private static ImmutableArray<ScenarioResult?> Transform(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method || ctx.TargetNode is not MethodDeclarationSyntax syntax)
        {
            return [];
        }

        var binding = ScenarioCaseBinding.Bind(ctx.SemanticModel, method, syntax);
        if (binding.Diagnostics.Count > 0)
        {
            return [];
        }

        var lineSpan = syntax.Identifier.GetLocation().GetLineSpan();
        ScenarioResult Parse(BoundScenarioCase? row) => GeneratorSafety.SafeParse(
            () => ScenarioParser.Lower(ctx.SemanticModel, method, syntax, row),
            lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
        return binding.Cases.Count == 0
            ? [Parse(null)]
            : [.. binding.Cases.Select(row => (ScenarioResult?)Parse(row))];
    }

    /// <summary>A parser diagnostic's span as a location in its source tree, found by path (the
    /// pipeline carries values, not syntax); an external-file location if the tree is gone.</summary>
    private static Location MakeLocation(ScenarioDiagnostic diagnostic, Compilation compilation)
    {
        var span = new TextSpan(diagnostic.SpanStart, diagnostic.SpanLength);
        var tree = compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == diagnostic.File);
        if (tree is not null && span.End <= tree.Length)
        {
            return Location.Create(tree, span);
        }

        return Location.Create(
            diagnostic.File,
            span,
            new LinePositionSpan(
                new LinePosition(diagnostic.Lines.StartLine, diagnostic.Lines.StartChar),
                new LinePosition(diagnostic.Lines.EndLine, diagnostic.Lines.EndChar)));
    }

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
