using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Raun.Generator.Syntax.Literals;

namespace Raun.Generator.Lowering;

/// <summary>
/// Lowers a <c>[Scenario]</c> method body into a <see cref="ParsedScenario"/>: it walks the
/// statements in source order, recognizes awaited Given/When/Then calls (singly, in tuples, in
/// arrays, or via a constant LINQ <c>.ToArray()</c>), and records each step's dataflow + source-order
/// dependencies, output binding, display name, and rewritten invocation.
/// Returns <c>null</c> when the body falls outside the supported subset (the analyzer reports why).
/// </summary>
internal sealed class ScenarioParser
{
    private readonly SemanticModel _model;
    private readonly IMethodSymbol _method;
    private readonly MethodDeclarationSyntax _syntax;

    // Step-output local -> the step(s) that produced it. Keyed by symbol, never by name: an argument
    // is lowered by binding its identifiers, so a lambda parameter or a sibling scope's local that
    // happens to share a name is a different key.
    private readonly Dictionary<ILocalSymbol, VarSource> _vars = new(SymbolEqualityComparer.Default);

    // Namespaces that contain the invoked DSL extension members; imported into the generated file
    // so `Given.PatientExists(...)` resolves there too. Kept as symbols: a using directive is built
    // from the namespace chain, never from a display string split back apart.
    private readonly HashSet<INamespaceSymbol> _dslNamespaces = new(SymbolEqualityComparer.Default);

    // Contended-resource uses accumulated from every [Uses<T>] site the scenario is subject to
    // (fqn -> token type + mode); Exclusive wins per type. The fqn is the ordering key only.
    private readonly Dictionary<string, (TypeSyntax Type, string Mode)> _uses =
        new(System.StringComparer.Ordinal);

    // How many steps so far share an identity key (operation + arguments as written). The ordinal
    // disambiguates genuinely identical steps and nothing else, which is what keeps a step's uid put
    // when an unrelated step is inserted before it or the step is wrapped in an `if`.
    private readonly Dictionary<string, int> _stepKeyOrdinals = new(System.StringComparer.Ordinal);

    // Indices introduced by the previous top-level statement (source-order barrier / join target).
    private List<int> _prevFrontier = [];

    private const string UnsupportedStatement = "This statement is not a shape the generator lowers";

    /// <summary>What the walk rejected first, for RAUN017: the innermost statement, or the offending
    /// part of a step argument, with why. Null while the walk succeeds.</summary>
    private (SyntaxNode Node, string Reason)? _rejection;

    /// <summary>A LINQ unroll's loop variable and its value for the element being built.</summary>
    private readonly record struct LoopElement(IParameterSymbol Variable, int Value);

    // Ordering-only predecessors the NEXT statement must wait for: the last steps of every arm of the
    // `if` that just closed. DependsOn cannot carry them (an arm may be not-taken, and DependsOn would
    // cascade that), so they ride on WaitsFor. Consumed and cleared by the statement that follows.
    private List<int> _pendingWaits = [];

    // Guards accumulated by the enclosing if/else arms; every step created inherits a snapshot.
    private readonly List<ParsedGuard> _guards = [];

    private int _nextIndex;
    private string _scenarioId = "";

    private readonly List<ParsedStep> _steps = [];

    private ScenarioParser(SemanticModel model, IMethodSymbol method, MethodDeclarationSyntax syntax)
    {
        _model = model;
        _method = method;
        _syntax = syntax;
    }

    private readonly record struct VarSource(bool IsArray, int Index, int[] Indices, TypeSyntax? ElementType)
    {
        /// <summary>The step(s) a read of this local depends on.</summary>
        public IEnumerable<int> Producers => IsArray ? Indices : [Index];

        public static VarSource Scalar(int index) => new(false, index, [], null);
        public static VarSource Array(int[] indices, TypeSyntax elementType) => new(true, -1, indices, elementType);
    }

    public static ParseOutcome TryParse(SemanticModel model, IMethodSymbol method, MethodDeclarationSyntax syntax)
        => new ScenarioParser(model, method, syntax).Parse();

    private ParseOutcome Parse()
    {
        var scenario = Lower();
        if (scenario is not null)
        {
            return new ParseOutcome(scenario, null);
        }

        // What the walk rejected, or the method name when there was no body to walk.
        var at = _rejection?.Node.GetLocation() ?? _syntax.Identifier.GetLocation();
        var lines = at.GetLineSpan();
        var name = _method.ContainingType.ToDisplayString(SymbolHelpers.NoGlobal) + "." + _method.Name;
        return new ParseOutcome(null, new ParseRejection(
            name,
            _rejection?.Reason ?? UnsupportedStatement,
            lines.Path,
            at.SourceSpan.Start,
            at.SourceSpan.Length,
            new SourceSpan(
                lines.Path,
                lines.StartLinePosition.Line, lines.StartLinePosition.Character,
                lines.EndLinePosition.Line, lines.EndLinePosition.Character)));
    }

    private ParsedScenario? Lower()
    {
        if (_syntax.Body is null)
        {
            return null;
        }

        var methodFullName = _method.ContainingType.ToDisplayString(SymbolHelpers.NoGlobal) + "." + _method.Name;
        _scenarioId = GenStableId.ForScenario(methodFullName);

        foreach (var statement in _syntax.Body.Statements)
        {
            if (!ParseStatement(statement))
            {
                return null; // reported as RAUN017 by TryParse, at _rejection
            }
        }

        // Always emitted: the generator cannot see OnTeardown calls (they happen at run time inside
        // DSL bodies), so emitting conditionally would let a registered cleanup fail silently. With
        // nothing registered the scheduler reports it NotTaken.
        var teardownIndex = _nextIndex++;
        _steps.Add(new ParsedStep
        {
            Index = teardownIndex,
            StepId = GenStableId.ForStep(_scenarioId, "teardown"),
            Phase = "Then",
            OperationName = "Teardown",
            HasResult = false,
            DisplayNameTemplate = "Teardown",
            IsTeardown = true,
            DependsOn = [],
        });

        // Uses declared on the scenario itself, its containing classes (nested outward), and the
        // assembly; the steps' DSL methods were merged as each step was lowered.
        AddUses(_method.GetAttributes());
        for (var type = _method.ContainingType; type is not null; type = type.ContainingType)
        {
            AddUses(type.GetAttributes());
        }

        AddUses(_model.Compilation.Assembly.GetAttributes());

        var usings = CollectUsings().ToList();

        // Ordered by display string: a HashSet's order is an implementation detail, and the emitted
        // using list has to be the same on every run.
        foreach (var ns in _dslNamespaces.OrderBy(
            n => n.ToDisplayString(SymbolHelpers.NoGlobal), System.StringComparer.Ordinal))
        {
            usings.Add(UsingDirective(TypeSyntaxFactory.NamespaceName(ns)));
        }

        return new ParsedScenario
        {
            MethodFullName = methodFullName,
            Namespace = _method.ContainingNamespace is { IsGlobalNamespace: false } declaringNamespace
                ? declaringNamespace.ToDisplayString(SymbolHelpers.NoGlobal)
                : "",
            TypeName = DeclaringTypeName(_method.ContainingType),
            SafeName = SafeName(methodFullName),
            ScenarioId = _scenarioId,
            DisplayName = AttributeReader.ScenarioDisplayName(_method) ?? _method.Name,
            ClassDisplayName = AttributeReader.ClassDisplayName(_method.ContainingType),
            TimeoutMs = AttributeReader.ScenarioTimeout(_method),
            TeardownPolicy = AttributeReader.TeardownPolicy(_method),
            SourceFile = Location(_syntax.Identifier, out var line),
            SourceLine = line,
            Steps = [.. _steps],
            Usings = usings,
            Uses = _uses
                .OrderBy(p => p.Key, System.StringComparer.Ordinal)
                .Select(p => new ParsedUse(p.Value.Type, p.Value.Mode, p.Key))
                .ToList(),
        };
    }

    /// <summary>Merges the [Uses] on one site into the scenario's set; Exclusive wins per type.</summary>
    private void AddUses(ImmutableArray<AttributeData> attributes)
    {
        foreach (var (resource, mode) in AttributeReader.Uses(attributes))
        {
            var fqn = resource.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!_uses.TryGetValue(fqn, out var existing) || existing.Mode != "Exclusive")
            {
                _uses[fqn] = (TypeSyntaxFactory.From(resource), mode);
            }
        }
    }

    /// <summary>The declaring type's simple name, with nesting joined by <c>+</c> (outermost first),
    /// which a dotted split of the method's full name cannot recover.</summary>
    private static string DeclaringTypeName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType)
        {
            parts.Add(t.Name);
        }

        parts.Reverse();
        return string.Join("+", parts);
    }

    private bool ParseStatement(StatementSyntax statement)
    {
        var ok = statement switch
        {
            LocalDeclarationStatementSyntax local => ParseLocalDeclaration(local),
            ExpressionStatementSyntax expr => ParseExpressionStatement(expr),
            IfStatementSyntax ifStatement => ParseIf(ifStatement),
            BlockSyntax block => ParseBlock(block),
            _ => false,
        };

        // Recursion unwinds leaf-first, so the first rejection recorded is the innermost one — an
        // argument's, when a step inside this statement refused one.
        if (!ok)
        {
            _rejection ??= (statement, UnsupportedStatement);
        }

        return ok;
    }

    private bool ParseBlock(BlockSyntax block)
    {
        foreach (var statement in block.Statements)
        {
            if (!ParseStatement(statement))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lowers <c>if (await Given.C(...)) A else B</c>. The condition is an ordinary node; each arm is
    /// walked with an extra guard pushed. Locals defined differently by the two arms become phi
    /// (merge) nodes at the closing brace — a definition map diff, which is all SSA needs when the
    /// control flow is structured (every merge point IS the closing brace).
    /// </summary>
    private bool ParseIf(IfStatementSyntax statement)
    {
        if (statement.Condition is not AwaitExpressionSyntax { Expression: InvocationExpressionSyntax call })
        {
            return false; // RAUN011
        }

        var condition = BuildStep(call, groupId: null, _prevFrontier);
        if (condition is null || !condition.HasResult)
        {
            return false; // RAUN011: a condition must produce a value
        }

        MarkAsCondition(condition);

        var parentVars = new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default);

        var thenArm = WalkArm(statement.Statement, condition.Index, whenValue: true, parentVars);
        if (thenArm is null)
        {
            return false;
        }

        var thenVars = thenArm.Value.Vars;
        Dictionary<ILocalSymbol, VarSource>? elseVars = null;
        var waits = new SortedSet<int>(thenArm.Value.Waits);
        if (statement.Else is { } elseClause)
        {
            var elseArm = WalkArm(elseClause.Statement, condition.Index, whenValue: false, parentVars);
            if (elseArm is null)
            {
                return false;
            }

            elseVars = elseArm.Value.Vars;
            waits.UnionWith(elseArm.Value.Waits);
        }

        // An empty arm's frontier is the condition itself, which the next statement already depends on.
        waits.Remove(condition.Index);

        // Rejoin: start from the parent map, then insert a phi for every local the arms disagree on.
        _vars.Clear();
        foreach (var pair in parentVars)
        {
            _vars[pair.Key] = pair.Value;
        }

        var frontier = new List<int>();
        foreach (var local in DifferingLocals(parentVars, thenVars, elseVars))
        {
            var mergeIndex = InsertMerge(local, condition.Index, parentVars, thenVars, elseVars);
            if (mergeIndex < 0)
            {
                return false;
            }

            frontier.Add(mergeIndex);
        }

        // A following statement must never DEPEND on an arm's node (DependsOn is all-of and an arm may
        // not run); it joins on the merges, or on the condition when there are none. It must still
        // WAIT for every arm's last steps, or it would run concurrently with the inside of the if —
        // that is what WaitsFor carries, and a not-taken arm does not cascade through it.
        Advance(frontier.Count > 0 ? frontier : [condition.Index], [.. waits]);
        return true;
    }

    /// <summary>Closes a top-level statement: the next statement joins on <paramref name="frontier"/>
    /// and additionally waits for <paramref name="waits"/> (ordering only).</summary>
    private void Advance(List<int> frontier, List<int>? waits = null)
    {
        _prevFrontier = frontier;
        _pendingWaits = waits ?? [];
    }

    private void MarkAsCondition(ParsedStep condition)
    {
        var position = _steps.FindIndex(s => s.Index == condition.Index);
        _steps[position] = _steps[position] with { ConditionCoercionType = condition.ResultType };
    }

    /// <summary>
    /// Walks one arm with <paramref name="whenValue"/> pushed onto the guard stack, on a child copy of
    /// the definition map. Returns that child map plus the arm's tail — its final frontier and any
    /// waits a nested <c>if</c> left unconsumed — which the statement after the enclosing <c>if</c>
    /// must wait for. Null when the arm is unsupported.
    /// </summary>
    private (Dictionary<ILocalSymbol, VarSource> Vars, List<int> Waits)? WalkArm(
        StatementSyntax arm, int conditionIndex, bool whenValue, Dictionary<ILocalSymbol, VarSource> parentVars)
    {
        var savedFrontier = _prevFrontier;
        var savedWaits = _pendingWaits;
        _vars.Clear();
        foreach (var pair in parentVars)
        {
            _vars[pair.Key] = pair.Value;
        }

        _guards.Add(new ParsedGuard(conditionIndex, whenValue));
        _prevFrontier = [conditionIndex];
        _pendingWaits = [];
        var ok = ParseStatement(arm);
        _guards.RemoveAt(_guards.Count - 1);

        var tail = new List<int>(_prevFrontier);
        tail.AddRange(_pendingWaits);
        _prevFrontier = savedFrontier;
        _pendingWaits = savedWaits;

        return ok ? (new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default), tail) : null;
    }

    /// <summary>Locals whose definition differs between the arms (or between an arm and the parent) —
    /// exactly the set that needs a phi. A local declared inside one arm is branch-local: it is absent
    /// from the parent and from the other arm (a sibling scope's same-named local is another symbol),
    /// and C# scoping already forbids its later use, so it is dropped. Ordered by name, then by
    /// declaration position, so merge nodes are inserted identically on every run.</summary>
    private static IEnumerable<ILocalSymbol> DifferingLocals(
        Dictionary<ILocalSymbol, VarSource> parentVars,
        Dictionary<ILocalSymbol, VarSource> thenVars,
        Dictionary<ILocalSymbol, VarSource>? elseVars)
    {
        var locals = thenVars.Keys
            .Concat(elseVars?.Keys ?? Enumerable.Empty<ILocalSymbol>())
            .Distinct<ILocalSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(l => l.Name, System.StringComparer.Ordinal)
            .ThenBy(l => l.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0);

        foreach (var local in locals)
        {
            var inThen = thenVars.TryGetValue(local, out var thenSource);
            var elseSource = default(VarSource);
            var inElse = elseVars is not null && elseVars.TryGetValue(local, out elseSource);
            var inParent = parentVars.TryGetValue(local, out var parentDef);

            // Declared before the `if` without a value (`Appointment a;`) and assigned in both arms is
            // the ordinary phi; anything else missing from the parent is branch-local.
            if (!inParent && !(inThen && inElse))
            {
                continue;
            }

            var thenDef = inThen ? thenSource : parentDef;
            var elseDef = inElse ? elseSource : parentDef;

            if (!thenDef.Equals(elseDef))
            {
                yield return local;
            }
        }
    }

    /// <summary>
    /// Inserts the phi for one local: a synthetic merge over the two arm definitions. When an arm did
    /// not redefine the local, that side is a synthetic PASS-THROUGH node — guarded on the opposite
    /// value, aliasing the parent definition — so the merge's sources stay mutually exclusive (what
    /// <c>ScenarioDefinition.Validate</c> requires) and the parent value flows through when the arm is
    /// not taken. Arrays are not mergeable; returns -1 (the analyzer rejects the shape).
    /// </summary>
    private int InsertMerge(
        ILocalSymbol local,
        int conditionIndex,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        Dictionary<ILocalSymbol, VarSource> thenVars,
        Dictionary<ILocalSymbol, VarSource>? elseVars)
    {
        var thenDef = Side(thenVars, whenValue: true);
        var elseDef = Side(elseVars, whenValue: false);
        if (thenDef < 0 || elseDef < 0)
        {
            return -1;
        }

        var producer = _steps.First(s => s.Index == thenDef);
        var index = _nextIndex++;
        var merge = new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "merge:" + local.Name + ":" + index),
            Phase = producer.Phase,
            OperationName = "Merge",
            HasResult = true,
            ResultType = producer.ResultType,
            DisplayNameTemplate = "«merge " + local.Name + "»",
            MergeSources = [thenDef, elseDef],
            IsSynthetic = true,
            Guards = [.. _guards],
            DependsOn = [],
        };

        _steps.Add(merge);
        _vars[local] = VarSource.Scalar(index);
        return index;

        int Side(Dictionary<ILocalSymbol, VarSource>? armVars, bool whenValue)
        {
            if (armVars is not null && armVars.TryGetValue(local, out var armSource))
            {
                return armSource.IsArray ? -1 : armSource.Index;
            }

            if (!parentVars.TryGetValue(local, out var parentSource) || parentSource.IsArray)
            {
                return -1;
            }

            return InsertPassThrough(local.Name, conditionIndex, whenValue, parentSource.Index);
        }
    }

    /// <summary>
    /// Stands in for the arm that did not redefine the local (the missing <c>else</c> of a bare
    /// <c>if</c>, or an arm that simply left the local alone): a synthetic node aliasing the parent
    /// definition, guarded on <paramref name="whenValue"/> — the value of the side it OCCUPIES, so the
    /// merge's two sources end up mutually exclusive, as <c>ScenarioDefinition.Validate</c> requires.
    /// </summary>
    private int InsertPassThrough(string name, int conditionIndex, bool whenValue, int parentDef)
    {
        var producer = _steps.First(s => s.Index == parentDef);
        var index = _nextIndex++;
        var guards = new List<ParsedGuard>(_guards) { new(conditionIndex, whenValue) };
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "phi:" + name + ":" + index),
            Phase = producer.Phase,
            OperationName = "Unchanged",
            HasResult = true,
            ResultType = producer.ResultType,
            DisplayNameTemplate = "«" + name + " unchanged»",
            MergeSources = [parentDef],
            IsSynthetic = true,
            Guards = guards,
            DependsOn = [],
        });

        return index;
    }

    private bool ParseLocalDeclaration(LocalDeclarationStatementSyntax local)
    {
        var variables = local.Declaration.Variables;
        if (variables.Count != 1)
        {
            return false;
        }

        // `Appointment appointment;` — a declaration with no initializer produces no step. It only
        // introduces the name; the definition arrives from an assignment, typically one per `if` arm,
        // and the definition-map diff turns those into a phi at the closing brace.
        if (variables[0].Initializer is null)
        {
            return true;
        }

        if (variables[0].Initializer?.Value is not AwaitExpressionSyntax await
            || _model.GetDeclaredSymbol(variables[0]) is not ILocalSymbol declared)
        {
            return false;
        }

        return ParseAwaited(await.Expression, binding: Binding.Single(declared));
    }

    private bool ParseExpressionStatement(ExpressionStatementSyntax statement)
    {
        return statement.Expression switch
        {
            AwaitExpressionSyntax bareAwait => ParseAwaited(bareAwait.Expression, binding: null),
            // An assignment binds locals; one that binds nothing (a field, a property) has nowhere to
            // put the value, and lowering it as a bare step would drop the write without a word.
            AssignmentExpressionSyntax { Right: AwaitExpressionSyntax await } assignment
                => Binding.FromAssignment(assignment.Left, _model) is { } binding && ParseAwaited(await.Expression, binding),
            _ => false,
        };

    }

    private bool ParseAwaited(ExpressionSyntax awaited, Binding? binding)
    {
        switch (awaited)
        {
            case InvocationExpressionSyntax invocation when IsToArray(invocation):
                return ParseLinqArray(invocation, binding);
            case InvocationExpressionSyntax invocation:
                return ParseSingleCall(invocation, binding);
            case TupleExpressionSyntax tuple:
                return ParseTuple(tuple, binding);
            case ArrayCreationExpressionSyntax array:
                return ParseArray(array.Initializer, binding);
            case ImplicitArrayCreationExpressionSyntax array:
                return ParseArray(array.Initializer, binding);
            default:
                return false;
        }
    }

    private bool ParseSingleCall(InvocationExpressionSyntax invocation, Binding? binding)
    {
        if (binding is { Kind: BindingKind.Tuple })
        {
            return false;
        }

        var step = BuildStep(invocation, groupId: null, _prevFrontier);
        if (step is null)
        {
            return false;
        }

        if (binding is { Kind: BindingKind.Single })
        {
            _vars[binding.Locals[0]] = VarSource.Scalar(step.Index);
        }

        Advance([step.Index]);
        return true;
    }

    private bool ParseTuple(TupleExpressionSyntax tuple, Binding? binding)
    {
        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        var locals = binding?.Locals;

        for (var i = 0; i < tuple.Arguments.Count; i++)
        {
            if (tuple.Arguments[i].Expression is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            var step = BuildStep(invocation, groupId, _prevFrontier);
            if (step is null)
            {
                return false;
            }

            if (locals is not null && i < locals.Count)
            {
                _vars[locals[i]] = VarSource.Scalar(step.Index);
            }

            frontier.Add(step.Index);
        }

        Advance(frontier);
        return true;
    }

    private bool ParseArray(InitializerExpressionSyntax? initializer, Binding? binding)
    {
        if (initializer is null || binding is { Kind: BindingKind.Tuple })
        {
            return false;
        }

        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        TypeSyntax elementType = PredefinedType(Token(SyntaxKind.ObjectKeyword));

        foreach (var element in initializer.Expressions)
        {
            if (element is not InvocationExpressionSyntax invocation)
            {
                return false;
            }

            var step = BuildStep(invocation, groupId, _prevFrontier);
            if (step is null)
            {
                return false;
            }

            elementType = step.ResultType;
            frontier.Add(step.Index);
        }

        if (binding is not null)
        {
            _vars[binding.Locals[0]] = VarSource.Array(frontier.ToArray(), elementType);
        }

        // An empty group ran nothing, so the step after it still follows the step before it.
        if (frontier.Count > 0)
        {
            Advance(frontier);
        }

        return true;
    }

    private bool ParseLinqArray(InvocationExpressionSyntax toArray, Binding? binding)
    {
        if (binding is { Kind: BindingKind.Tuple })
        {
            return false;
        }

        // Shape: Enumerable.Range(start, count).Select(i => <DSL call using i>).ToArray()
        if (toArray.Expression is not MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax selectInv } toArrayMember
            || toArrayMember.Name.Identifier.Text != "ToArray")
        {
            return false;
        }

        if (selectInv.Expression is not MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax rangeInv } selectMember
            || selectMember.Name.Identifier.Text != "Select"
            || selectInv.ArgumentList.Arguments.Count != 1
            || selectInv.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax lambda)
        {
            return false;
        }

        if (rangeInv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Range" }
            || rangeInv.ArgumentList.Arguments.Count != 2)
        {
            return false;
        }

        var startConst = _model.GetConstantValue(rangeInv.ArgumentList.Arguments[0].Expression);
        var countConst = _model.GetConstantValue(rangeInv.ArgumentList.Arguments[1].Expression);
        if (!startConst.HasValue || !countConst.HasValue
            || startConst.Value is not int start || countConst.Value is not int count)
        {
            return false;
        }

        if (lambda.Body is not InvocationExpressionSyntax bodyCall)
        {
            return false;
        }

        if (_model.GetDeclaredSymbol(lambda.Parameter) is not IParameterSymbol loopVariable)
        {
            return false;
        }

        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        TypeSyntax elementType = PredefinedType(Token(SyntaxKind.ObjectKeyword));

        for (var k = 0; k < count; k++)
        {
            // Each element is the lambda body with the loop variable bound to that element's value.
            var step = BuildStep(bodyCall, groupId, _prevFrontier, new LoopElement(loopVariable, start + k));
            if (step is null)
            {
                return false;
            }

            elementType = step.ResultType;
            frontier.Add(step.Index);
        }

        if (binding is not null)
        {
            _vars[binding.Locals[0]] = VarSource.Array(frontier.ToArray(), elementType);
        }

        // An empty group ran nothing, so the step after it still follows the step before it.
        if (frontier.Count > 0)
        {
            Advance(frontier);
        }

        return true;
    }

    private static bool IsToArray(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToArray" };

    /// <summary>
    /// Builds a step from a DSL invocation in the scenario's tree. <paramref name="loop"/> binds a LINQ
    /// unroll's loop variable to the value of the element being built. Null, with the rejection
    /// recorded, when an argument cannot be lowered.
    /// </summary>
    private ParsedStep? BuildStep(
        InvocationExpressionSyntax invocation,
        string? groupId,
        List<int> sourceOrderDeps,
        LoopElement? loop = null)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return null;
        }

        var phase = SymbolHelpers.PhaseOf(member.Expression, _model);
        if (phase is null)
        {
            return null;
        }

        if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        if (!SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType))
        {
            return null;
        }

        // Every argument goes through the one lowering the analyzer also runs: a violation refuses
        // the scenario here and is RAUN007 there, at the same node, for the same reason.
        ExpressionSyntax? StepValue(ISymbol symbol) => symbol switch
        {
            ILocalSymbol local when _vars.TryGetValue(local, out var source) => Spell(source),
            IParameterSymbol parameter when loop is { } element
                && SymbolEqualityComparer.Default.Equals(parameter, element.Variable) => Num(element.Value),
            _ => null,
        };

        var lowering = ArgumentLowering.LowerCall(_model, invocation, StepValue);
        if (lowering.Violations.Any())
        {
            var violation = lowering.Violations.First();
            _rejection ??= (violation.Node, violation.Describe());
            return null;
        }

        var dslNamespace = method.ContainingType?.ContainingNamespace;
        if (dslNamespace is { IsGlobalNamespace: false })
        {
            _dslNamespaces.Add(dslNamespace);
        }

        var index = _nextIndex++;
        var operation = member.Name.Identifier.Text;

        // Source order, plus every step whose output an argument reads.
        var deps = new SortedSet<int>(sourceOrderDeps);
        foreach (var read in lowering.Reads)
        {
            deps.UnionWith(_vars[read.Local].Producers);
        }

        var written = invocation.ArgumentList.Arguments;
        var lowered = lowering.Arguments.Select(a => a.Node).ToList();
        var wantsCtx = SymbolHelpers.WantsContext(method, written.Count);
        var call = BuildCall(invocation, member, lowering.TypeArguments?.Node, lowered, wantsCtx);
        var resourceClaims = BuildResourceClaims(written, method, resultType is not null, lowered);
        AddUses(method.GetAttributes());

        var (template, formatExpr) = DisplayNameBuilder.Build(_model, method, written, lowered);

        var step = new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, StepKey(operation, invocation, loop)),
            Phase = phase,
            OperationName = operation,
            HasResult = resultType is not null,
            ResultType = resultType is null
                ? PredefinedType(Token(SyntaxKind.ObjectKeyword))
                : TypeSyntaxFactory.From(resultType),
            InvokeCall = call,
            DisplayNameTemplate = template,
            FormatExpression = formatExpr,
            GroupId = groupId,
            TimeoutMs = AttributeReader.StepTimeout(method),
            SourceFile = Location(invocation, out var line),
            SourceLine = line,
            CallSpan = SpanOf(invocation),
            DependsOn = [.. deps],
            WaitsFor = [.. _pendingWaits.Where(w => !deps.Contains(w))],
            ResourceClaims = resourceClaims,
            Guards = [.. _guards],
        };

        _steps.Add(step);
        return step;
    }

    /// <summary>
    /// How a step-output local is read inside an emitted step: a scalar is
    /// <c>__inputs.Get&lt;T&gt;(i)</c>, an array-bound group a <c>new T[] { … }</c> over its elements'
    /// gets.
    /// </summary>
    private ExpressionSyntax Spell(VarSource source)
    {
        if (!source.IsArray)
        {
            return InputsGet(_steps.First(s => s.Index == source.Index).ResultType, source.Index);
        }

        var elementType = source.ElementType!;
        return ArrayCreationExpression(
                ArrayType(elementType).WithRankSpecifiers(SingletonList(
                    ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(
                        OmittedArraySizeExpression())))))
            .WithInitializer(InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SeparatedList<ExpressionSyntax>(source.Indices.Select(i => InputsGet(elementType, i)))));
    }

    /// <summary><c>__inputs.Get&lt;type&gt;(index)</c>.</summary>
    private static InvocationExpressionSyntax InputsGet(TypeSyntax type, int index)
        => InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("__inputs"),
                    GenericName(Identifier("Get"))
                        .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(type)))))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(Num(index)))));

    /// <summary>
    /// The DSL invocation as the generated file will call it: the original receiver and method, its
    /// type and value arguments lowered, and <c>__ctx</c> appended when the method takes a trailing
    /// <c>ScenarioContext</c> the scenario left out. Outer trivia is dropped — the call is re-hosted
    /// inside a lambda, and the emitter formats the whole file.
    /// </summary>
    private static InvocationExpressionSyntax BuildCall(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member,
        TypeArgumentListSyntax? typeArguments,
        List<ArgumentSyntax> arguments,
        bool appendCtx)
    {
        var emitted = arguments.ToList();
        if (appendCtx)
        {
            emitted.Add(Argument(IdentifierName("__ctx")));
        }

        var callee = member.Name is GenericNameSyntax generic && typeArguments is not null
            ? member.WithName(generic.WithTypeArgumentList(typeArguments))
            : member;

        return invocation
            .WithExpression(callee)
            .WithArgumentList(ArgumentList(SeparatedList(emitted)))
            .WithoutTrivia();
    }

    /// <summary>
    /// Lowers the method's resource role attributes into <see cref="ResourceRoleClaim"/>s: one per
    /// role-bearing parameter (its lowered argument expression), then one for a return role when the
    /// step yields a value (using <c>__r</c>). Claims read the SAME lowered arguments as the call, so a
    /// claim names exactly the value the step receives. Empty when the method declares no roles ⇒ the
    /// emitter inserts nothing.
    /// </summary>
    private static List<ResourceRoleClaim> BuildResourceClaims(
        SeparatedSyntaxList<ArgumentSyntax> written,
        IMethodSymbol method,
        bool hasResult,
        List<ArgumentSyntax> lowered)
    {
        var claims = new List<ResourceRoleClaim>();

        // The lowered argument bound to the parameter at `position`, or null when the call left it out.
        ExpressionSyntax? ArgumentFor(string parameterName, int position)
            => ArgumentIndex(written, parameterName, position) is var i and >= 0
                ? lowered[i].Expression.WithoutTrivia()
                : null;

        // A producer's lineage target: Subject.Return is the step's own value; a parameter name is
        // that parameter's argument. Null when the name resolves to no supplied argument (the analyzer
        // reports it as RAUN010).
        ExpressionSyntax? TargetFor(string name)
        {
            if (name == AttributeReader.ReturnSubject)
            {
                return ReturnValue;
            }

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].Name == name)
                {
                    return ArgumentFor(name, i);
                }
            }

            return null;
        }

        // Emits the lineage relations a producing subject declares via [Created]/[Loaded]/[Edited]'s
        // References/Consumes: one Reference/Consume claim per resolvable target, the subject expression
        // riding along (the runtime records subject→target). Emitted BEFORE the subject's own role claim,
        // so effect order stays target-lineage-then-subject (e.g. Reference, Consume, then Create).
        void EmitLineage((ImmutableArray<string> References, ImmutableArray<string> Consumes) lineage, ExpressionSyntax subjectExpression)
        {
            foreach (var target in lineage.References)
            {
                if (TargetFor(target) is { } expr)
                {
                    claims.Add(new ResourceRoleClaim("Reference", expr, IsReturn: false) { SubjectExpressions = [subjectExpression] });
                }
            }

            foreach (var target in lineage.Consumes)
            {
                if (TargetFor(target) is { } expr)
                {
                    claims.Add(new ResourceRoleClaim("Consume", expr, IsReturn: false) { SubjectExpressions = [subjectExpression] });
                }
            }
        }

        for (var p = 0; p < method.Parameters.Length; p++)
        {
            var parameter = method.Parameters[p];
            var role = AttributeReader.ParameterRole(parameter);
            if (role is null)
            {
                continue;
            }

            if (ArgumentFor(parameter.Name, p) is not { } expression)
            {
                // No supplied argument. The trailing-ScenarioContext case is correct (nothing to claim).
                // The omitted-optional case (a declared role on a defaulted param that the call left out)
                // is a deliberate, known gap: the role silently vanishes. Diagnosing it is intentionally
                // deferred to RAUN009 (Task 9); a future reader should not treat this skip as a bug.
                continue;
            }

            if (role == "Edit")
            {
                EmitLineage(AttributeReader.ProducerLineage(parameter.GetAttributes()), expression);
            }

            claims.Add(new ResourceRoleClaim(role, expression, IsReturn: false));
        }

        if (hasResult && AttributeReader.ReturnRole(method) is { } returnRole)
        {
            var lineage = AttributeReader.ProducerLineage(method.GetReturnTypeAttributes());
            if (lineage.References.IsEmpty && lineage.Consumes.IsEmpty)
            {
                lineage = AttributeReader.ProducerLineage(method.GetAttributes());
            }

            EmitLineage(lineage, ReturnValue);
            claims.Add(new ResourceRoleClaim(returnRole, ReturnValue, IsReturn: true));
        }

        return claims;
    }

    /// <summary>The step's own return value inside the emitted lambda.</summary>
    private static IdentifierNameSyntax ReturnValue => IdentifierName("__r");

    /// <summary>
    /// The index of the argument bound to the parameter at <paramref name="position"/>: a named
    /// argument (<c>name: value</c>) matching <paramref name="parameterName"/> if present, else the
    /// positional argument at that index. -1 when neither exists (e.g. an omitted optional parameter).
    /// </summary>
    internal static int ArgumentIndex(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        string parameterName,
        int position)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].NameColon?.Name.Identifier.Text == parameterName)
            {
                return i;
            }
        }

        return position < arguments.Count && arguments[position].NameColon is null ? position : -1;
    }

    /// <summary>The argument <see cref="ArgumentIndex"/> finds, or null.</summary>
    internal static ArgumentSyntax? FindArgument(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        string parameterName,
        int position)
        => ArgumentIndex(arguments, parameterName, position) is var i and >= 0 ? arguments[i] : null;

    // The emitter dedupes the merged using set, so no need to dedupe here. Trivia is stripped: the
    // scenario file's comments and formatting have no business in the generated one.
    private IEnumerable<UsingDirectiveSyntax> CollectUsings()
        => _syntax.SyntaxTree.GetCompilationUnitRoot().Usings.Select(u => u.WithoutTrivia());

    /// <summary>
    /// The identity a step's uid is hashed from: the DSL operation, the arguments exactly as the
    /// author wrote them (token texts, so reformatting and comments do not count), and an ordinal
    /// among earlier steps with the same identity. Position deliberately plays no part — the old
    /// ordinal-only key renamed every later step whenever one was inserted, which broke saved
    /// filters and an IDE's "re-run this test". The WRITTEN arguments are read, not the lowered
    /// ones: lowering names steps by index, which would put position straight back in. The one
    /// substitution is a LINQ unroll's loop variable, spelled as its value so each element differs.
    /// </summary>
    private string StepKey(string operation, InvocationExpressionSyntax invocation, LoopElement? loop)
    {
        var builder = new StringBuilder(operation);
        builder.Append('(');
        foreach (var token in invocation.ArgumentList.DescendantTokens())
        {
            builder.Append(loop is { } element && IsReferenceTo(token, element.Variable)
                ? element.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : token.Text);
        }

        builder.Append(')');
        var identity = builder.ToString();

        _stepKeyOrdinals.TryGetValue(identity, out var ordinal);
        _stepKeyOrdinals[identity] = ordinal + 1;
        return identity + ":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool IsReferenceTo(SyntaxToken token, ISymbol symbol)
        => token.Parent is IdentifierNameSyntax identifier
            && SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, symbol);

    private static string? Location(SyntaxNode node, out int line)
    {
        var span = node.GetLocation().GetLineSpan();
        line = span.StartLinePosition.Line + 1;
        return span.Path;
    }

    private static SourceSpan? SpanOf(SyntaxNode node)
    {
        var s = node.GetLocation().GetLineSpan();
        if (string.IsNullOrEmpty(s.Path))
        {
            return null;
        }

        return new SourceSpan(
            s.Path,
            s.StartLinePosition.Line, s.StartLinePosition.Character,
            s.EndLinePosition.Line, s.EndLinePosition.Character);
    }

    private static string? Location(SyntaxToken token, out int line)
    {
        var span = token.GetLocation().GetLineSpan();
        line = span.StartLinePosition.Line + 1;
        return span.Path;
    }

    private static string SafeName(string methodFullName)
    {
        var sb = new StringBuilder();
        foreach (var c in methodFullName)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.ToString();
    }
}
