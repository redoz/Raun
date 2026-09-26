using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Raun.Generator;
using Raun.Generator.Diagnostics;
using Raun.Generator.Lowering;

namespace Raun.Generator.Analysis;

/// <summary>
/// The rules that are not about lowering a scenario body: <c>[StepName]</c> placeholders (RAUN008),
/// resource roles and lineage on DSL methods (RAUN009/010), a cleanup's context (RAUN014), and
/// contended resources (RAUN015/016). What a <c>[Scenario]</c> body may contain is the parser's to
/// judge alone — it reports through the generator — so no second walker can disagree with it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScenarioAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Descriptors.UnhandledException,
        Descriptors.UnboundPlaceholder,
        Descriptors.MissingResourceRole,
        Descriptors.InvalidLineageSubject,
        Descriptors.StepContextInCleanup,
        Descriptors.ContendedResourceKind,
        Descriptors.InertContendedResourceUse,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    /// <summary>
    /// RAUN016 needs a compilation-wide view: whether ANY scenario uses a resource exclusively, and how
    /// many distinct scenarios use it at all, to decide whether the resource's declaration is
    /// permanently inert. A single syntax node can't answer that, so this registers its own
    /// per-compilation accumulator (a scenario's reduced use is recorded as each [Scenario] method is
    /// visited) and reports once at compilation end. Concurrent execution is enabled for this analyzer,
    /// so the accumulator is a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by symbol identity,
    /// and each entry serializes its own writes.
    /// </summary>
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var usage = new ConcurrentDictionary<INamedTypeSymbol, ResourceUsageAccumulator>(SymbolEqualityComparer.Default);

        context.RegisterSyntaxNodeAction(
            ctx => AnalyzeScenarioResourceUse(ctx, usage), SyntaxKind.MethodDeclaration);
        context.RegisterCompilationEndAction(ctx => ReportInertContendedResources(ctx, usage));
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        try
        {
            AnalyzeContendedResource(context, (INamedTypeSymbol)context.Symbol);
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnhandledException, context.Symbol.Locations.FirstOrDefault(), GeneratorSafety.Describe(ex)));
        }
    }

    /// <summary>RAUN015: a type implementing Raun.IContendedResource carries exactly one kind attribute,
    /// and a pool has a capacity of at least 1. The runtime gate throws the same; catching it here
    /// keeps it out of the first parallel run.</summary>
    private static void AnalyzeContendedResource(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (!IsContendedResource(type))
        {
            return;
        }

        var kind = ReadContendedResourceKind(type);
        if (kind.KindsDeclared != 1 || kind.PoolTooSmall)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.ContendedResourceKind, type.Locations.FirstOrDefault(), type.Name));
        }
    }

    private static bool IsContendedResource(INamedTypeSymbol type)
        => type.AllInterfaces.Any(i => i.Name == "IContendedResource" && i.ContainingNamespace.ToDisplayString() == "Raun");

    /// <summary>One resource type's declared kind, read once and shared by RAUN015 (is the declaration
    /// itself valid — exactly one kind, and a pool capacity of at least 1?) and RAUN016 (given a valid
    /// declaration, is it Shared — the only kind whose capacity never binds?). The declared pool
    /// capacity itself isn't carried: RAUN016 no longer needs it (see <see cref="ReportInertContendedResources"/>),
    /// and RAUN015 only needs to know whether it was valid.</summary>
    private readonly record struct ContendedResourceKindInfo(int KindsDeclared, string Kind, bool PoolTooSmall);

    private static ContendedResourceKindInfo ReadContendedResourceKind(INamedTypeSymbol type)
    {
        var kinds = 0;
        var kind = "";
        var capacity = 0;
        var poolTooSmall = false;
        foreach (var attr in type.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "ExclusiveResourceAttribute":
                    kinds++;
                    kind = "Exclusive";
                    break;
                case "SharedResourceAttribute":
                    kinds++;
                    kind = "Shared";
                    break;
                case "PooledResourceAttribute":
                    kinds++;
                    kind = "Pooled";
                    if (attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int cap)
                    {
                        capacity = cap;
                    }

                    poolTooSmall = attr.ConstructorArguments.Length != 1
                        || attr.ConstructorArguments[0].Value is not int || capacity < 1;
                    break;
            }
        }

        return new ContendedResourceKindInfo(kinds, kind, poolTooSmall);
    }

    /// <summary>Per resource type: every scenario using it, whether any of them uses it exclusively
    /// (after per-scenario reduction — the same type wins the same way <c>ScenarioParser.AddUses</c>
    /// reduces multiple sites on one scenario: Exclusive beats Shared), and every use-site location seen
    /// (for RAUN016's fallback location — see <see cref="PickFallbackLocation"/>). Thread-safe: entries
    /// are shared across concurrent syntax-node-action invocations.</summary>
    private sealed class ResourceUsageAccumulator
    {
        private readonly object _gate = new();
        private readonly HashSet<IMethodSymbol> _scenarios = new(SymbolEqualityComparer.Default);
        private readonly List<Location> _useLocations = new();
        private bool _anyExclusive;

        public void Record(IMethodSymbol scenario, bool exclusive, Location useLocation)
        {
            lock (_gate)
            {
                _scenarios.Add(scenario);
                _anyExclusive |= exclusive;
                _useLocations.Add(useLocation);
            }
        }

        public (int ScenarioCount, bool AnyExclusive, IReadOnlyList<Location> UseLocations) Snapshot()
        {
            lock (_gate)
            {
                return (_scenarios.Count, _anyExclusive, _useLocations.ToArray());
            }
        }
    }

    private static void AnalyzeScenarioResourceUse(
        SyntaxNodeAnalysisContext context, ConcurrentDictionary<INamedTypeSymbol, ResourceUsageAccumulator> usage)
    {
        try
        {
            AnalyzeScenarioResourceUseCore(context, usage);
        }
        catch (Exception ex)
        {
            var location = ((MethodDeclarationSyntax)context.Node).Identifier.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnhandledException, location, GeneratorSafety.Describe(ex)));
        }
    }

    private static void AnalyzeScenarioResourceUseCore(
        SyntaxNodeAnalysisContext context, ConcurrentDictionary<INamedTypeSymbol, ResourceUsageAccumulator> usage)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(method) is not IMethodSymbol symbol
            || !HasAttribute(symbol, "ScenarioAttribute"))
        {
            return;
        }

        // Reduce every site's [Uses<T>] to one entry per resource type for THIS scenario — Exclusive
        // wins per type, same as ScenarioParser.AddUses — before folding into the compilation-wide
        // accumulator, so a scenario that both shares and exclusively uses a resource (via different
        // sites) correctly counts as an exclusive user of it.
        var perScenario = new Dictionary<INamedTypeSymbol, (bool Exclusive, Location Location)>(SymbolEqualityComparer.Default);
        foreach (var (resource, mode, location) in CollectScenarioUses(context, method, symbol))
        {
            var exclusive = mode == "Exclusive";
            perScenario[resource] = perScenario.TryGetValue(resource, out var existing)
                ? (existing.Exclusive || exclusive, existing.Location)
                : (exclusive, location);
        }

        foreach (var pair in perScenario)
        {
            usage.GetOrAdd(pair.Key, static _ => new ResourceUsageAccumulator())
                .Record(symbol, pair.Value.Exclusive, pair.Value.Location);
        }
    }

    /// <summary>Every <c>[Uses&lt;T&gt;]</c> a scenario is subject to, matching what <c>ScenarioParser</c>
    /// unions onto a scenario: every DSL method its body calls, the scenario method itself, its
    /// containing types (walked outward), and the compilation's assembly attributes. Walking every
    /// invocation (rather than reproducing the parser's lowering) is a safe superset for the
    /// exclusivity flag: a use the generator wouldn't actually emit can only make RAUN016 stay silent,
    /// never fire falsely, because it can only add an exclusive user that doesn't really exist. It is
    /// NOT a safe superset for the scenario count, though: a <c>[Uses&lt;T&gt;]</c>-bearing method
    /// invoked somewhere the generator would not lower still counts its scenario toward the
    /// two-scenario floor.</summary>
    private static IEnumerable<(INamedTypeSymbol Resource, string Mode, Location Location)> CollectScenarioUses(
        SyntaxNodeAnalysisContext context, MethodDeclarationSyntax method, IMethodSymbol symbol)
    {
        if (method.Body is not null)
        {
            foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol called)
                {
                    foreach (var use in UsesWithLocation(called.GetAttributes()))
                    {
                        yield return use;
                    }
                }
            }
        }

        foreach (var use in UsesWithLocation(symbol.GetAttributes()))
        {
            yield return use;
        }

        for (var type = symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            foreach (var use in UsesWithLocation(type.GetAttributes()))
            {
                yield return use;
            }
        }

        foreach (var use in UsesWithLocation(context.SemanticModel.Compilation.Assembly.GetAttributes()))
        {
            yield return use;
        }
    }

    /// <summary>Like <see cref="AttributeReader.Uses"/>, but paired with the application-site location
    /// of the specific <c>[Uses&lt;T&gt;]</c> attribute, for RAUN016's fallback location when the
    /// resource type has none of its own in this compilation.</summary>
    private static IEnumerable<(INamedTypeSymbol Resource, string Mode, Location Location)> UsesWithLocation(
        ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            foreach (var (resource, mode) in AttributeReader.Uses(ImmutableArray.Create(attr)))
            {
                var location = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;
                yield return (resource, mode, location);
            }
        }
    }

    /// <summary>
    /// RAUN016: report once per resource type that is <c>[SharedResource]</c>, used by two or more
    /// scenarios, and never used exclusively by any of them. That combination is the only one that is
    /// permanently inert — adding scenarios never makes a declaration like that start binding, because
    /// shared uses never block each other and a Shared resource never gains an exclusive slot no matter
    /// how many scenarios join. A Pooled resource is out of scope even at or under its capacity: that is
    /// not-yet-binding, not permanently inert — one more scenario using it makes the pool start
    /// serializing access, the same "suite still being built" shape as the deliberate under-two-scenario
    /// carve-out below, and warning on it would be the behaviour most likely to feel hostile while a
    /// suite is still being assembled. An Exclusive-kind resource is out of scope too: capacity 1 always
    /// binds once two or more scenarios use it, so it never qualifies here.
    /// </summary>
    private static void ReportInertContendedResources(
        CompilationAnalysisContext context, ConcurrentDictionary<INamedTypeSymbol, ResourceUsageAccumulator> usage)
    {
        foreach (var pair in usage)
        {
            try
            {
                var resource = pair.Key;
                var (scenarioCount, anyExclusive, useLocations) = pair.Value.Snapshot();

                // Deliberately excluded: a resource used by fewer than two scenarios is inert today but
                // is the normal state while a suite is being built up — warning here would fire on work
                // in progress, not a real gap.
                if (scenarioCount < 2 || anyExclusive)
                {
                    continue;
                }

                var kind = ReadContendedResourceKind(resource);
                if (kind.KindsDeclared != 1 || kind.PoolTooSmall)
                {
                    // RAUN015 already flags a malformed kind declaration — including a pool whose
                    // capacity isn't valid — so a type it has already condemned must never also reach
                    // RAUN016's reporting logic.
                    continue;
                }

                if (kind.Kind != "Shared")
                {
                    // Pooled (at or under capacity): not-yet-binding, not permanently inert — see the
                    // method doc. Exclusive: capacity 1 always binds once scenarioCount >= 2 (guarded
                    // above).
                    continue;
                }

                var location = resource.Locations.FirstOrDefault(l => l.IsInSource) ?? PickFallbackLocation(useLocations);
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.InertContendedResourceUse, location, resource.Name));
            }
            catch
            {
                // The accumulator was already built by AnalyzeScenarioResourceUse, which has its own
                // RAUN000 catch; this loop only reads it and picks a location deterministically, so a
                // throw here means a bug in this analyzer, not in user code. Swallow rather than report:
                // reporting RAUN000 from a compilation-end action would force it to carry
                // WellKnownDiagnosticTags.CompilationEnd (RS1037), which is false for RAUN000 — it is
                // also reported live from syntax-node and symbol actions, where a generator crash most
                // needs to stay visible rather than risk deferral to full-solution analysis.
            }
        }
    }

    /// <summary>RAUN016's fallback location when the resource type has no in-source declaration of its
    /// own (it's declared in another assembly): the lowest of every recorded <c>[Uses&lt;T&gt;]</c>
    /// site, ordered by file path then by span start (both ordinal). Deterministic regardless of which
    /// syntax-node-action invocation records first — <see cref="Initialize"/> enables concurrent
    /// execution, so "the first one recorded" is otherwise first-writer-wins and can move between
    /// builds of identical source, moving a warnings-as-errors diagnostic's location along with it.</summary>
    private static Location PickFallbackLocation(IReadOnlyList<Location> useLocations)
    {
        if (useLocations.Count == 0)
        {
            return Location.None;
        }

        return useLocations
            .OrderBy(l => l.SourceTree?.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(l => l.SourceSpan.Start)
            .First();
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        try
        {
            AnalyzeCleanupRegistration(context, (InvocationExpressionSyntax)context.Node);
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnhandledException, context.Node.GetLocation(), GeneratorSafety.Describe(ex)));
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        try
        {
            AnalyzeMethodCore(context);
        }
        catch (Exception ex)
        {
            var location = ((MethodDeclarationSyntax)context.Node).Identifier.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnhandledException, location, GeneratorSafety.Describe(ex)));
        }
    }

    private static void AnalyzeMethodCore(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(method) is IMethodSymbol symbol
            && HasAttribute(symbol, "StepNameAttribute"))
        {
            AnalyzeStepName(context, symbol);
            AnalyzeStepResources(context, symbol);
        }
    }

    /// <summary>
    /// RAUN014: a cleanup lambda handed to <c>ScenarioContext.OnTeardown</c> must not reach for a
    /// <c>ScenarioContext</c> declared outside it — typically the step's own <c>ctx</c>. The cleanup
    /// runs inside the Teardown node after that step has been reported, so anything logged or attached
    /// through the captured context is lost. The lambda's own parameter and
    /// <c>ScenarioContext.Current</c> ARE the teardown context and stay clean.
    /// </summary>
    private static void AnalyzeCleanupRegistration(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        // Syntactic pre-filter: this runs for every invocation in the compilation, so only pay for a
        // symbol lookup when the member is literally named OnTeardown (plain or ?. access).
        var memberName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };
        if (memberName != "OnTeardown")
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !IsScenarioContext(method.ContainingType))
        {
            return;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is not AnonymousFunctionExpressionSyntax cleanup)
            {
                continue;
            }

            foreach (var identifier in cleanup.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if ((identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier)
                    || identifier.Parent is NameColonSyntax or NameEqualsSyntax)
                {
                    continue;
                }

                var symbol = context.SemanticModel.GetSymbolInfo(identifier).Symbol;
                var type = symbol switch
                {
                    IParameterSymbol parameter => parameter.Type,
                    ILocalSymbol local => local.Type,
                    _ => null,
                };

                if (symbol is null || type is null || !IsScenarioContext(type) || DeclaredInside(symbol, cleanup))
                {
                    continue;
                }

                Report(context, Descriptors.StepContextInCleanup, identifier.GetLocation(), identifier.Identifier.Text);
            }
        }
    }

    private static bool IsScenarioContext(ITypeSymbol type)
        => type.Name == "ScenarioContext"
            && type.ContainingNamespace?.ToDisplayString(SymbolHelpers.NoGlobal) == "Raun";

    /// <summary>True when <paramref name="symbol"/> is declared within <paramref name="scope"/> (a lambda's
    /// own parameter or a local it introduces), so it is not a capture from the enclosing step.</summary>
    private static bool DeclaredInside(ISymbol symbol, SyntaxNode scope)
        => symbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == scope.SyntaxTree && scope.Span.Contains(r.Span));

    private static void AnalyzeStepName(SyntaxNodeAnalysisContext context, IMethodSymbol method)
    {
        var attribute = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "StepNameAttribute");
        if (attribute is not { ConstructorArguments.Length: > 0 }
            || attribute.ConstructorArguments[0].Value is not string template)
        {
            return;
        }

        var parameters = method.Parameters.Select(p => p.Name).ToImmutableHashSet();
        foreach (var token in TemplateTokenizer.Tokenize(template))
        {
            if (token.IsPlaceholder && !parameters.Contains(token.Text))
            {
                var location = method.Locations.FirstOrDefault() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.UnboundPlaceholder, location, token.Text, method.Name));
            }
        }
    }

    private static void AnalyzeStepResources(SyntaxNodeAnalysisContext context, IMethodSymbol method)
    {
        var paramNames = method.Parameters.Select(p => p.Name).ToImmutableHashSet();

        // Producing subjects and the targets they name via [Created]/[Loaded]/[Edited]'s
        // References/Consumes. A producer is keyed by its own "self" name — a parameter name, or the
        // return sentinel — so it cannot name itself as a lineage target.
        var producers = new List<(string Self, ImmutableArray<string> Targets, Location Location)>();

        foreach (var parameter in method.Parameters)
        {
            if (AttributeReader.ParameterRole(parameter) != "Edit")
            {
                continue;
            }

            var (refs, cons) = AttributeReader.ProducerLineage(parameter.GetAttributes());
            if (!refs.IsEmpty || !cons.IsEmpty)
            {
                var loc = parameter.Locations.FirstOrDefault() ?? method.Locations.FirstOrDefault() ?? Location.None;
                producers.Add((parameter.Name, refs.AddRange(cons), loc));
            }
        }

        var hasReturnRole = SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var returnType)
            && returnType is not null
            && AttributeReader.ReturnRole(method) is not null;
        if (hasReturnRole)
        {
            var (refs, cons) = AttributeReader.ProducerLineage(method.GetReturnTypeAttributes());
            if (refs.IsEmpty && cons.IsEmpty)
            {
                (refs, cons) = AttributeReader.ProducerLineage(method.GetAttributes());
            }

            if (!refs.IsEmpty || !cons.IsEmpty)
            {
                producers.Add((AttributeReader.ReturnSubject, refs.AddRange(cons), method.Locations.FirstOrDefault() ?? Location.None));
            }
        }

        // A bare parameter named as a lineage target is "covered" for RAUN009: being named confers the
        // Reference/Consume role (and its shared effect), so it needs no attribute of its own.
        var coveredByLineage = producers
            .SelectMany(p => p.Targets)
            .Where(name => name != AttributeReader.ReturnSubject && paramNames.Contains(name))
            .ToImmutableHashSet();

        foreach (var parameter in method.Parameters)
        {
            if (IsResourceType(parameter.Type)
                && AttributeReader.ParameterRole(parameter) is null
                && !coveredByLineage.Contains(parameter.Name))
            {
                var location = parameter.Locations.FirstOrDefault() ?? method.Locations.FirstOrDefault() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.MissingResourceRole, location, "parameter", parameter.Name));
            }
        }

        if (SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType)
            && resultType is not null
            && IsResourceType(resultType)
            && AttributeReader.ReturnRole(method) is null)
        {
            var location = method.Locations.FirstOrDefault() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.MissingResourceRole, location, "return", method.Name));
        }

        // RAUN010: each lineage target must name a parameter, or the return when the step yields a
        // subject (Subject.Return); a producer may not name itself.
        foreach (var producer in producers)
        {
            foreach (var target in producer.Targets)
            {
                var valid = target == AttributeReader.ReturnSubject
                    ? hasReturnRole && producer.Self != AttributeReader.ReturnSubject
                    : paramNames.Contains(target) && target != producer.Self;
                if (!valid)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.InvalidLineageSubject, producer.Location, target, method.Name));
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> participates in the resource model — i.e. it implements
    /// <c>Raun.IResource&lt;TSelf&gt;</c> (arity 1) or <c>Raun.IResourceIdentity</c>. A trailing
    /// <c>Raun.ScenarioContext</c> param is naturally excluded.
    /// </summary>
    private static bool IsResourceType(ITypeSymbol type)
        => type.AllInterfaces.Any(i =>
            i.ContainingNamespace?.ToDisplayString(SymbolHelpers.NoGlobal) == "Raun"
            && ((i.Name == "IResource" && i.Arity == 1) || i.Name == "IResourceIdentity"));

    private static bool HasAttribute(IMethodSymbol method, string attributeName)
        => method.GetAttributes().Any(a =>
            a.AttributeClass?.Name == attributeName
            && a.AttributeClass.ContainingNamespace?.ToDisplayString(SymbolHelpers.NoGlobal) == "Raun");

    private static void Report(
        SyntaxNodeAnalysisContext context,
        DiagnosticDescriptor descriptor,
        Location location,
        params object[] args)
        => context.ReportDiagnostic(Diagnostic.Create(descriptor, location, args));
}
