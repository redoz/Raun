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
using Raun.Generator.Lowering;

namespace Raun.Generator.Analysis;

/// <summary>
/// Validates that <c>[Scenario]</c> methods stay inside the lowerable subset, and that
/// <c>[StepName]</c> templates bind to parameters, reporting clear RAUN diagnostics otherwise.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScenarioAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Descriptors.UnhandledException,
        Descriptors.MustBeAsyncTask,
        Descriptors.UnsupportedStatement,
        Descriptors.UnsupportedControlFlow,
        Descriptors.NotADslCall,
        Descriptors.InvalidReturnType,
        Descriptors.InvalidGroupElement,
        Descriptors.InvalidArgument,
        Descriptors.UnboundPlaceholder,
        Descriptors.MissingResourceRole,
        Descriptors.InvalidLineageSubject,
        Descriptors.InvalidCondition,
        Descriptors.UnmergeableLocal,
        Descriptors.ConflictingParallelAccess,
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
        if (context.SemanticModel.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
        {
            return;
        }

        if (HasAttribute(symbol, "StepNameAttribute"))
        {
            AnalyzeStepName(context, symbol);
            AnalyzeStepResources(context, symbol);
        }

        if (!HasAttribute(symbol, "ScenarioAttribute"))
        {
            return;
        }

        if (!symbol.IsAsync || !SymbolHelpers.IsVoidTaskLike(symbol.ReturnType))
        {
            Report(context, Descriptors.MustBeAsyncTask, method.Identifier.GetLocation(), symbol.Name);
        }

        if (method.Body is null)
        {
            return;
        }

        var stepOutputs = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        foreach (var statement in method.Body.Statements)
        {
            AnalyzeStatement(context, statement, stepOutputs);
        }
    }

    /// <summary>
    /// An `if` is supported when its condition is an awaited phase-marker call whose result is usable
    /// as a C# condition. Each arm is analyzed with the same step-output set; an assignment inside an
    /// arm to a local that is not already a step output is RAUN012 (nothing to merge against).
    /// </summary>
    private static void AnalyzeIf(
        SyntaxNodeAnalysisContext context,
        IfStatementSyntax statement,
        HashSet<ILocalSymbol> stepOutputs)
    {
        if (statement.Condition is not AwaitExpressionSyntax { Expression: InvocationExpressionSyntax invocation }
            || invocation.Expression is not MemberAccessExpressionSyntax member
            || SymbolHelpers.PhaseOf(member.Expression, context.SemanticModel) is null)
        {
            Report(context, Descriptors.InvalidCondition, statement.Condition.GetLocation());
        }
        else
        {
            AnalyzeDslCall(context, invocation, stepOutputs, Descriptors.NotADslCall);

            if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                && SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType)
                && (resultType is null || !IsUsableAsCondition(resultType, context.SemanticModel.Compilation)))
            {
                Report(context, Descriptors.InvalidCondition, statement.Condition.GetLocation());
            }
        }

        // RAUN012 is about definitions on EVERY path, so it must be decided against the definitions
        // that existed before the branch — hence the snapshot, taken before the arms are analyzed
        // (analyzing an arm adds that arm's own declarations to the set).
        var parentOutputs = new HashSet<ILocalSymbol>(stepOutputs, SymbolEqualityComparer.Default);
        var thenAssigned = CollectAssignedLocals(context, statement.Statement);
        var elseAssigned = statement.Else is { } elseBranch
            ? CollectAssignedLocals(context, elseBranch.Statement)
            : [];

        AnalyzeStatement(context, statement.Statement, stepOutputs);
        if (statement.Else is { } elseClause)
        {
            AnalyzeStatement(context, elseClause.Statement, stepOutputs);
        }

        foreach (var pair in thenAssigned.Concat(elseAssigned))
        {
            var local = pair.Key;

            // Assigning in BOTH arms is the ordinary phi and needs no prior definition. Assigning in
            // only one arm is fine too, as long as a step produced the value before the branch — that
            // definition becomes the pass-through side of the merge. Neither means some path reaches
            // the merge with no node behind it.
            var definedEverywhere = thenAssigned.ContainsKey(local) && elseAssigned.ContainsKey(local);
            if (!definedEverywhere && !parentOutputs.Contains(local))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.UnmergeableLocal, pair.Value.GetLocation(), local.Name));
                continue;
            }

            // The merge is a step-produced definition for everything after the `if`.
            stepOutputs.Add(local);
        }
    }

    /// <summary>Locals an arm re-assigns from an awaited call (<c>x = await When.Y(...)</c>), mapped to
    /// the identifier that names them, for diagnostic locations.</summary>
    private static Dictionary<ILocalSymbol, IdentifierNameSyntax> CollectAssignedLocals(
        SyntaxNodeAnalysisContext context, StatementSyntax arm)
    {
        var assigned = new Dictionary<ILocalSymbol, IdentifierNameSyntax>(SymbolEqualityComparer.Default);
        foreach (var assignment in arm.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment is { Left: IdentifierNameSyntax identifier, Right: AwaitExpressionSyntax }
                && context.SemanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol local)
            {
                assigned[local] = identifier;
            }
        }

        return assigned;
    }

    /// <summary>
    /// True when <paramref name="type"/> can drive a C# <c>if</c>: it is <c>bool</c>, defines
    /// <c>operator true</c>, or has an implicit conversion to <c>bool</c>. <c>bool?</c> is correctly
    /// rejected — C# rejects it too.
    /// </summary>
    private static bool IsUsableAsCondition(ITypeSymbol type, Compilation compilation)
    {
        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return true;
        }

        if (type.GetMembers("op_True").Any())
        {
            return true;
        }

        var boolType = compilation.GetSpecialType(SpecialType.System_Boolean);
        var conversion = compilation.ClassifyConversion(type, boolType);
        return conversion.IsImplicit && conversion.IsUserDefined;
    }

    private static void AnalyzeStatement(
        SyntaxNodeAnalysisContext context,
        StatementSyntax statement,
        HashSet<ILocalSymbol> stepOutputs)
    {
        switch (statement)
        {
            case EmptyStatementSyntax:
                return;

            case LocalDeclarationStatementSyntax local:
                AnalyzeLocalDeclaration(context, local, stepOutputs);
                return;

            case ExpressionStatementSyntax expr:
                AnalyzeExpressionStatement(context, expr, stepOutputs);
                return;

            case BlockSyntax block:
                foreach (var inner in block.Statements)
                {
                    AnalyzeStatement(context, inner, stepOutputs);
                }

                return;

            case IfStatementSyntax ifStatement:
                AnalyzeIf(context, ifStatement, stepOutputs);
                return;

            case ForStatementSyntax or ForEachStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax
                or TryStatementSyntax or UsingStatementSyntax or LockStatementSyntax
                or GotoStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax
                or ThrowStatementSyntax or YieldStatementSyntax or LabeledStatementSyntax
                or FixedStatementSyntax or CheckedStatementSyntax or UnsafeStatementSyntax
                or LocalFunctionStatementSyntax or ReturnStatementSyntax:
                Report(context, Descriptors.UnsupportedControlFlow, statement.GetLocation());
                return;

            default:
                Report(context, Descriptors.UnsupportedStatement, statement.GetLocation());
                return;
        }
    }

    private static void AnalyzeLocalDeclaration(
        SyntaxNodeAnalysisContext context,
        LocalDeclarationStatementSyntax local,
        HashSet<ILocalSymbol> stepOutputs)
    {
        var variables = local.Declaration.Variables;
        if (variables.Count != 1)
        {
            Report(context, Descriptors.UnsupportedStatement, local.GetLocation());
            return;
        }

        // `Appointment appointment;` — a declaration with no initializer introduces the name only; the
        // definition arrives from an assignment in each `if` arm and becomes a phi at the closing brace.
        if (variables[0].Initializer is null)
        {
            return;
        }

        if (variables[0].Initializer?.Value is not AwaitExpressionSyntax await)
        {
            Report(context, Descriptors.UnsupportedStatement, local.GetLocation());
            return;
        }

        AnalyzeAwaited(context, await.Expression, stepOutputs);

        if (context.SemanticModel.GetDeclaredSymbol(variables[0]) is ILocalSymbol declared)
        {
            stepOutputs.Add(declared);
        }
    }

    private static void AnalyzeExpressionStatement(
        SyntaxNodeAnalysisContext context,
        ExpressionStatementSyntax statement,
        HashSet<ILocalSymbol> stepOutputs)
    {
        switch (statement.Expression)
        {
            case AwaitExpressionSyntax bareAwait:
                AnalyzeAwaited(context, bareAwait.Expression, stepOutputs);
                return;

            case AssignmentExpressionSyntax { Right: AwaitExpressionSyntax await } assignment:
                AnalyzeAwaited(context, await.Expression, stepOutputs);

                // The same Binding the parser builds: the locals this assignment defines are step
                // outputs from here on. Anything else on the left (a field, a property) has no slot
                // in the graph for the value, and the parser refuses it too.
                if (Binding.FromAssignment(assignment.Left, context.SemanticModel) is { } binding)
                {
                    stepOutputs.UnionWith(binding.Locals);
                }
                else
                {
                    Report(context, Descriptors.UnsupportedStatement, assignment.Left.GetLocation());
                }

                return;

            default:
                Report(context, Descriptors.UnsupportedStatement, statement.GetLocation());
                return;
        }
    }

    private static void AnalyzeAwaited(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax awaited,
        HashSet<ILocalSymbol> stepOutputs)
    {
        switch (awaited)
        {
            case InvocationExpressionSyntax invocation
                when invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToArray" }:
                AnalyzeLinqArray(context, invocation, stepOutputs);
                return;

            case InvocationExpressionSyntax invocation:
                AnalyzeDslCall(context, invocation, stepOutputs, Descriptors.NotADslCall);
                return;

            case TupleExpressionSyntax tuple:
                foreach (var arg in tuple.Arguments)
                {
                    AnalyzeGroupElement(context, arg.Expression, stepOutputs);
                }

                AnalyzeGroupConflicts(context, tuple.Arguments.Select(a => a.Expression), stepOutputs);
                return;

            case ArrayCreationExpressionSyntax array:
                AnalyzeArrayElements(context, array.Initializer, awaited, stepOutputs);
                return;

            case ImplicitArrayCreationExpressionSyntax array:
                AnalyzeArrayElements(context, array.Initializer, awaited, stepOutputs);
                return;

            default:
                Report(context, Descriptors.UnsupportedStatement, awaited.GetLocation());
                return;
        }
    }

    private static void AnalyzeArrayElements(
        SyntaxNodeAnalysisContext context,
        InitializerExpressionSyntax? initializer,
        ExpressionSyntax awaited,
        HashSet<ILocalSymbol> stepOutputs)
    {
        if (initializer is null)
        {
            Report(context, Descriptors.UnsupportedStatement, awaited.GetLocation());
            return;
        }

        foreach (var element in initializer.Expressions)
        {
            AnalyzeGroupElement(context, element, stepOutputs);
        }

        AnalyzeGroupConflicts(context, initializer.Expressions, stepOutputs);
    }

    private static void AnalyzeGroupElement(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax element,
        HashSet<ILocalSymbol> stepOutputs)
    {
        if (element is InvocationExpressionSyntax invocation)
        {
            AnalyzeDslCall(context, invocation, stepOutputs, Descriptors.InvalidGroupElement);
        }
        else
        {
            Report(context, Descriptors.InvalidGroupElement, element.GetLocation());
        }
    }

    private static void AnalyzeDslCall(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        HashSet<ILocalSymbol> stepOutputs,
        DiagnosticDescriptor notDslDescriptor)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || SymbolHelpers.PhaseOf(member.Expression, context.SemanticModel) is null)
        {
            Report(context, notDslDescriptor, invocation.GetLocation());
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && !SymbolHelpers.TryUnwrapReturn(method.ReturnType, out _))
        {
            Report(context, Descriptors.InvalidReturnType, invocation.GetLocation(), method.Name);
        }

        ReportArgumentViolations(context, invocation, stepOutputs);
    }

    /// <summary>
    /// RAUN007: runs the generator's own argument lowering over the call and reports what it refused —
    /// the same node and the same reason the generator's RAUN017 would carry, because it is the same pass.
    /// </summary>
    private static void ReportArgumentViolations(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        HashSet<ILocalSymbol> stepOutputs,
        IParameterSymbol? loopVariable = null)
    {
        foreach (var violation in LowerArguments(context, invocation, stepOutputs, loopVariable).Violations)
        {
            Report(context, Descriptors.InvalidArgument, violation.Node.GetLocation(), violation.Subject, violation.Reason);
        }
    }

    /// <summary>The call's arguments lowered as the parser lowers them. The analyzer only needs to know
    /// WHICH symbols carry a step's value, not how the generator spells them, so each keeps its name.</summary>
    private static LoweredCall LowerArguments(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        HashSet<ILocalSymbol> stepOutputs,
        IParameterSymbol? loopVariable = null)
        => ArgumentLowering.LowerCall(context.SemanticModel, invocation, symbol =>
            (symbol is ILocalSymbol local && stepOutputs.Contains(local))
            || SymbolEqualityComparer.Default.Equals(symbol, loopVariable)
                ? SyntaxFactory.IdentifierName(symbol.Name)
                : null);

    private static void AnalyzeLinqArray(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax toArray,
        HashSet<ILocalSymbol> stepOutputs)
    {
        // Enumerable.Range(constStart, constCount).Select(i => <DSL call>).ToArray()
        if (toArray.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax selectInv }
            && selectInv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" }
            && selectInv.ArgumentList.Arguments.Count == 1
            && selectInv.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax { Body: InvocationExpressionSyntax body } lambda
            && selectInv.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax rangeInv }
            && rangeInv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Range" }
            && rangeInv.ArgumentList.Arguments.Count == 2
            && context.SemanticModel.GetConstantValue(rangeInv.ArgumentList.Arguments[0].Expression).Value is int
            && context.SemanticModel.GetConstantValue(rangeInv.ArgumentList.Arguments[1].Expression).Value is int count)
        {
            // Validate the per-element call resolves to a DSL member with a valid return type.
            if (body.Expression is MemberAccessExpressionSyntax bodyMember
                && SymbolHelpers.PhaseOf(bodyMember.Expression, context.SemanticModel) is not null)
            {
                if (context.SemanticModel.GetSymbolInfo(body).Symbol is IMethodSymbol method
                    && !SymbolHelpers.TryUnwrapReturn(method.ReturnType, out _))
                {
                    Report(context, Descriptors.InvalidReturnType, body.GetLocation(), method.Name);
                }

                var loopVariable = context.SemanticModel.GetDeclaredSymbol(lambda.Parameter) as IParameterSymbol;
                ReportArgumentViolations(context, body, stepOutputs, loopVariable);
                AnalyzeUnrollConflicts(context, body, count, stepOutputs, loopVariable);
            }
            else
            {
                Report(context, Descriptors.InvalidGroupElement, body.GetLocation());
            }

            return;
        }

        Report(context, Descriptors.UnsupportedStatement, toArray.GetLocation());
    }

    /// <summary>One parallel-group element's declared access to a prior step's output.</summary>
    private readonly record struct GroupAccess(
        ILocalSymbol Local, string Verb, bool Exclusive, string Operation, Location Location);

    /// <summary>
    /// RAUN013 for one parallel group (tuple or array). Its elements run concurrently, so two of them
    /// passing the same step-output local to role-bearing parameters conflict when at least one role
    /// mutates. Concurrency inside a scenario comes ONLY from these groups — sequential statements
    /// join on the previous frontier — so no graph is needed: the group IS the concurrency. Two
    /// different locals that resolve to one runtime identity are the scheduler's conflict ledger's job.
    /// </summary>
    private static void AnalyzeGroupConflicts(
        SyntaxNodeAnalysisContext context,
        IEnumerable<ExpressionSyntax> elements,
        HashSet<ILocalSymbol> stepOutputs)
    {
        var earlier = new List<GroupAccess>();
        foreach (var element in elements)
        {
            if (element is not InvocationExpressionSyntax invocation)
            {
                continue;
            }

            var accesses = CollectAccesses(context, invocation, stepOutputs);
            foreach (var access in accesses)
            {
                foreach (var prior in earlier)
                {
                    if (SymbolEqualityComparer.Default.Equals(prior.Local, access.Local)
                        && (prior.Exclusive || access.Exclusive))
                    {
                        Report(
                            context,
                            Descriptors.ConflictingParallelAccess,
                            access.Location,
                            prior.Operation,
                            access.Operation,
                            access.Local.Name,
                            $"{prior.Verb}/{access.Verb}");
                    }
                }
            }

            earlier.AddRange(accesses);
        }
    }

    /// <summary>RAUN013 for a LINQ unroll: the lambda body becomes <paramref name="count"/> concurrent
    /// copies of one call, so a mutating role on an outer step-output local conflicts with itself.</summary>
    private static void AnalyzeUnrollConflicts(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax body,
        int count,
        HashSet<ILocalSymbol> stepOutputs,
        IParameterSymbol? loopVariable)
    {
        if (count < 2)
        {
            return;
        }

        foreach (var access in CollectAccesses(context, body, stepOutputs, loopVariable))
        {
            if (access.Exclusive)
            {
                Report(
                    context,
                    Descriptors.ConflictingParallelAccess,
                    access.Location,
                    access.Operation,
                    access.Operation,
                    access.Local.Name,
                    $"{access.Verb}/{access.Verb}");
            }
        }
    }

    /// <summary>
    /// The step-output locals a DSL call passes to role-bearing parameters, with each role's verb.
    /// Parameter roles come from <c>[Read]/[Edited]/[Deleted]</c>; a bare parameter named in a
    /// producer's <c>References</c>/<c>Consumes</c> carries the shared Reference/Consume role. Return
    /// roles never appear: a step's return is its own output, shared with no sibling. Argument-to-
    /// parameter matching mirrors the parser's, so named arguments bind correctly.
    /// </summary>
    private static List<GroupAccess> CollectAccesses(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        HashSet<ILocalSymbol> stepOutputs,
        IParameterSymbol? loopVariable = null)
    {
        var accesses = new List<GroupAccess>();
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            return accesses;
        }

        var operation = member.Name.Identifier.Text;
        var lineageVerbs = LineageVerbs(method);
        var arguments = invocation.ArgumentList.Arguments;
        var lowered = LowerArguments(context, invocation, stepOutputs, loopVariable);

        for (var p = 0; p < method.Parameters.Length; p++)
        {
            var parameter = method.Parameters[p];
            var verb = AttributeReader.ParameterRole(parameter)
                ?? (lineageVerbs.TryGetValue(parameter.Name, out var lineageVerb) ? lineageVerb : null);
            var index = ScenarioParser.ArgumentIndex(arguments, parameter.Name, p);
            if (verb is null || index < 0)
            {
                continue;
            }

            // Mirrors LifecycleVerb.ToLockMode in the runtime assembly: Edit/Delete exclude, the rest share.
            var exclusive = verb is "Edit" or "Delete";
            foreach (var read in lowered.Arguments[index].Reads)
            {
                accesses.Add(new GroupAccess(read.Local, verb, exclusive, operation, read.Node.GetLocation()));
            }
        }

        return accesses;
    }

    /// <summary>Parameter name → Reference/Consume for every parameter a producer on this method names
    /// as a lineage target (return-role producers and <c>[Edited]</c>-parameter producers alike).</summary>
    private static Dictionary<string, string> LineageVerbs(IMethodSymbol method)
    {
        var verbs = new Dictionary<string, string>(StringComparer.Ordinal);

        var returnLineage = AttributeReader.ProducerLineage(method.GetReturnTypeAttributes());
        if (returnLineage.References.IsEmpty && returnLineage.Consumes.IsEmpty)
        {
            returnLineage = AttributeReader.ProducerLineage(method.GetAttributes());
        }

        AddLineage(verbs, returnLineage);
        foreach (var parameter in method.Parameters)
        {
            if (AttributeReader.ParameterRole(parameter) == "Edit")
            {
                AddLineage(verbs, AttributeReader.ProducerLineage(parameter.GetAttributes()));
            }
        }

        return verbs;

        static void AddLineage(
            Dictionary<string, string> verbs,
            (ImmutableArray<string> References, ImmutableArray<string> Consumes) lineage)
        {
            foreach (var target in lineage.References)
            {
                verbs[target] = "Reference";
            }

            foreach (var target in lineage.Consumes)
            {
                verbs[target] = "Consume";
            }
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
