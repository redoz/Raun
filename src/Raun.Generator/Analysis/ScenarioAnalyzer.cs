using System;
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
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
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
        if (!type.AllInterfaces.Any(i => i.Name == "IContendedResource" && i.ContainingNamespace.ToDisplayString() == "Raun"))
        {
            return;
        }

        var kinds = 0;
        var poolTooSmall = false;
        foreach (var attr in type.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "ExclusiveResourceAttribute":
                case "SharedResourceAttribute":
                    kinds++;
                    break;
                case "PooledResourceAttribute":
                    kinds++;
                    poolTooSmall = attr.ConstructorArguments.Length != 1
                        || attr.ConstructorArguments[0].Value is not int capacity
                        || capacity < 1;
                    break;
            }
        }

        if (kinds != 1 || poolTooSmall)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.ContendedResourceKind, type.Locations.FirstOrDefault(), type.Name));
        }
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
                RecordDeconstructedLocals(context, assignment.Left, stepOutputs);
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

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            foreach (var identifier in argument.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                // Skip member names (`x.Member`) and argument labels (`name:`); flag any other
                // identifier that binds to a local which isn't a prior step output.
                if ((identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier)
                    || identifier.Parent is NameColonSyntax or NameEqualsSyntax)
                {
                    continue;
                }

                if (context.SemanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol local
                    && !stepOutputs.Contains(local))
                {
                    Report(context, Descriptors.InvalidArgument, identifier.GetLocation(), identifier.Identifier.Text);
                }
            }
        }
    }

    private static void AnalyzeLinqArray(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax toArray,
        HashSet<ILocalSymbol> stepOutputs)
    {
        // Enumerable.Range(constStart, constCount).Select(i => <DSL call>).ToArray()
        if (toArray.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax selectInv }
            && selectInv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Select" }
            && selectInv.ArgumentList.Arguments.Count == 1
            && selectInv.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax { Body: InvocationExpressionSyntax body }
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

                AnalyzeUnrollConflicts(context, body, count, stepOutputs);
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
        HashSet<ILocalSymbol> stepOutputs)
    {
        if (count < 2)
        {
            return;
        }

        foreach (var access in CollectAccesses(context, body, stepOutputs))
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
        HashSet<ILocalSymbol> stepOutputs)
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

        for (var p = 0; p < method.Parameters.Length; p++)
        {
            var parameter = method.Parameters[p];
            var verb = AttributeReader.ParameterRole(parameter)
                ?? (lineageVerbs.TryGetValue(parameter.Name, out var lineageVerb) ? lineageVerb : null);
            if (verb is null)
            {
                continue;
            }

            var argument = ScenarioParser.FindArgument(arguments, parameter.Name, p);
            if (argument is null)
            {
                continue;
            }

            // Mirrors LifecycleVerb.ToLockMode in the runtime assembly: Edit/Delete exclude, the rest share.
            var exclusive = verb is "Edit" or "Delete";
            foreach (var identifier in argument.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if ((identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier)
                    || identifier.Parent is NameColonSyntax or NameEqualsSyntax)
                {
                    continue;
                }

                if (context.SemanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol local
                    && stepOutputs.Contains(local))
                {
                    accesses.Add(new GroupAccess(local, verb, exclusive, operation, identifier.GetLocation()));
                }
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

    private static void RecordDeconstructedLocals(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax left,
        HashSet<ILocalSymbol> stepOutputs)
    {
        foreach (var designation in left.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>())
        {
            if (context.SemanticModel.GetDeclaredSymbol(designation) is ILocalSymbol local)
            {
                stepOutputs.Add(local);
            }
        }
    }

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
