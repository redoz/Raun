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

    // Variable name -> the step(s) that produced it.
    private readonly Dictionary<string, VarSource> _vars = [];

    // Namespaces that contain the invoked DSL extension members; imported into the generated file
    // so `Given.PatientExists(...)` resolves there too. Kept as symbols: a using directive is built
    // from the namespace chain, never from a display string split back apart.
    private readonly HashSet<INamespaceSymbol> _dslNamespaces = new(SymbolEqualityComparer.Default);

    // Contended-resource uses accumulated from every [Uses<T>] site the scenario is subject to
    // (fqn -> token type + mode); Exclusive wins per type. The fqn is the ordering key only.
    private readonly Dictionary<string, (TypeSyntax Type, string Mode)> _uses =
        new(System.StringComparer.Ordinal);

    // Indices introduced by the previous top-level statement (source-order barrier / join target).
    private List<int> _prevFrontier = [];

    /// <summary>The innermost statement the walk rejected, for RAUN017. Null while the walk succeeds.</summary>
    private StatementSyntax? _rejected;

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

        // The innermost rejected statement, or the method name when there was no body to walk.
        var at = _rejected is not null ? _rejected.GetLocation() : _syntax.Identifier.GetLocation();
        var lines = at.GetLineSpan();
        var name = _method.ContainingType.ToDisplayString(SymbolHelpers.NoGlobal) + "." + _method.Name;
        return new ParseOutcome(null, new ParseRejection(
            name,
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
                return null; // reported as RAUN017 by TryParse, at _rejected
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

        // Recursion unwinds leaf-first, so the first statement recorded is the innermost one.
        if (!ok)
        {
            _rejected ??= statement;
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

        var parentVars = new Dictionary<string, VarSource>(_vars);

        var thenArm = WalkArm(statement.Statement, condition.Index, whenValue: true, parentVars);
        if (thenArm is null)
        {
            return false;
        }

        var thenVars = thenArm.Value.Vars;
        Dictionary<string, VarSource>? elseVars = null;
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
        foreach (var name in DifferingLocals(parentVars, thenVars, elseVars))
        {
            var mergeIndex = InsertMerge(name, condition.Index, parentVars, thenVars, elseVars);
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
    private (Dictionary<string, VarSource> Vars, List<int> Waits)? WalkArm(
        StatementSyntax arm, int conditionIndex, bool whenValue, Dictionary<string, VarSource> parentVars)
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

        return ok ? (new Dictionary<string, VarSource>(_vars), tail) : null;
    }

    /// <summary>Locals whose definition differs between the arms (or between an arm and the parent) —
    /// exactly the set that needs a phi. A local defined only inside one arm and absent from the parent
    /// is branch-local: C# scoping already forbids its later use, so it is dropped.</summary>
    private static IEnumerable<string> DifferingLocals(
        Dictionary<string, VarSource> parentVars,
        Dictionary<string, VarSource> thenVars,
        Dictionary<string, VarSource>? elseVars)
    {
        var names = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (var name in thenVars.Keys)
        {
            names.Add(name);
        }

        if (elseVars is not null)
        {
            foreach (var name in elseVars.Keys)
            {
                names.Add(name);
            }
        }

        foreach (var name in names)
        {
            var inThen = thenVars.TryGetValue(name, out var thenSource);
            var inElse = elseVars is not null && elseVars.TryGetValue(name, out _);
            var inParent = parentVars.TryGetValue(name, out var parentSource);

            if (!inParent && !(inThen && inElse))
            {
                continue; // branch-local
            }

            var thenDef = inThen ? thenSource : parentSource;
            var elseDef = elseVars is not null && elseVars.TryGetValue(name, out var elseSource)
                ? elseSource
                : parentSource;

            if (!thenDef.Equals(elseDef))
            {
                yield return name;
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
        string name,
        int conditionIndex,
        Dictionary<string, VarSource> parentVars,
        Dictionary<string, VarSource> thenVars,
        Dictionary<string, VarSource>? elseVars)
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
            StepId = GenStableId.ForStep(_scenarioId, "merge:" + name + ":" + index),
            Phase = producer.Phase,
            OperationName = "Merge",
            HasResult = true,
            ResultType = producer.ResultType,
            DisplayNameTemplate = "«merge " + name + "»",
            MergeSources = [thenDef, elseDef],
            IsSynthetic = true,
            Guards = [.. _guards],
            DependsOn = [],
        };

        _steps.Add(merge);
        _vars[name] = VarSource.Scalar(index);
        return index;

        int Side(Dictionary<string, VarSource>? armVars, bool whenValue)
        {
            if (armVars is not null && armVars.TryGetValue(name, out var armSource))
            {
                return armSource.IsArray ? -1 : armSource.Index;
            }

            if (!parentVars.TryGetValue(name, out var parentSource) || parentSource.IsArray)
            {
                return -1;
            }

            return InsertPassThrough(name, conditionIndex, whenValue, parentSource.Index);
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

        if (variables[0].Initializer?.Value is not AwaitExpressionSyntax await)
        {
            return false;
        }

        return ParseAwaited(await.Expression, binding: Binding.Single(variables[0].Identifier.Text));
    }

    private bool ParseExpressionStatement(ExpressionStatementSyntax statement)
    {
        return statement.Expression switch
        {
            AwaitExpressionSyntax bareAwait => ParseAwaited(bareAwait.Expression, binding: null),
            AssignmentExpressionSyntax { Right: AwaitExpressionSyntax await } assignment => ParseAwaited(await.Expression, binding: Binding.FromAssignment(assignment.Left)),
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
            _vars[binding.Names[0]] = VarSource.Scalar(step.Index);
        }

        Advance([step.Index]);
        return true;
    }

    private bool ParseTuple(TupleExpressionSyntax tuple, Binding? binding)
    {
        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        var names = binding?.Names;

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

            if (names is not null && i < names.Count)
            {
                _vars[names[i]] = VarSource.Scalar(step.Index);
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
            _vars[binding.Names[0]] = VarSource.Array(frontier.ToArray(), elementType);
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

        var loopVar = lambda.Parameter.Identifier.Text;
        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        TypeSyntax elementType = PredefinedType(Token(SyntaxKind.ObjectKeyword));

        for (var k = 0; k < count; k++)
        {
            var value = start + k;
            // Replace the loop variable with the constant value for this element.
            var substituted = (InvocationExpressionSyntax)new IdentifierReplacer(
                new Dictionary<string, ExpressionSyntax> { [loopVar] = Num(value) }).Visit(bodyCall);

            var step = BuildStep(substituted, groupId, _prevFrontier, semanticNode: bodyCall);
            if (step is null)
            {
                return false;
            }

            elementType = step.ResultType;
            frontier.Add(step.Index);
        }

        if (binding is not null)
        {
            _vars[binding.Names[0]] = VarSource.Array(frontier.ToArray(), elementType);
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
    /// Builds a step from a DSL invocation. <paramref name="semanticNode"/> is the invocation to use
    /// for semantic lookups when the emitted call has been syntactically rewritten (LINQ unroll).
    /// </summary>
    private ParsedStep? BuildStep(
        InvocationExpressionSyntax invocation,
        string? groupId,
        List<int> sourceOrderDeps,
        InvocationExpressionSyntax? semanticNode = null)
    {
        var lookup = semanticNode ?? invocation;
        if (lookup.Expression is not MemberAccessExpressionSyntax member)
        {
            return null;
        }

        var phase = SymbolHelpers.PhaseOf(member.Expression, _model);
        if (phase is null)
        {
            return null;
        }

        if (_model.GetSymbolInfo(lookup).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        if (!SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType))
        {
            return null;
        }

        var dslNamespace = method.ContainingType?.ContainingNamespace;
        if (dslNamespace is { IsGlobalNamespace: false })
        {
            _dslNamespaces.Add(dslNamespace);
        }

        var index = _nextIndex++;
        var operation = member.Name.Identifier.Text;

        // Dataflow dependencies: variables referenced in the (original) arguments.
        var dataflow = CollectDataflowDeps(invocation);
        var deps = new SortedSet<int>(sourceOrderDeps);
        foreach (var d in dataflow)
        {
            deps.Add(d);
        }

        var replacements = BuildReplacements();
        var wantsCtx = SymbolHelpers.WantsContext(method, invocation.ArgumentList.Arguments.Count);
        var call = BuildCall(invocation, replacements, wantsCtx);
        var resourceClaims = BuildResourceClaims(invocation, method, resultType is not null, replacements);
        AddUses(method.GetAttributes());

        var (template, formatExpr) = DisplayNameBuilder.Build(_model, method, invocation.ArgumentList.Arguments, replacements);

        var step = new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, operation + ":" + index),
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

    private List<int> CollectDataflowDeps(InvocationExpressionSyntax invocation)
    {
        var deps = new List<int>();
        foreach (var identifier in invocation.ArgumentList.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (_vars.TryGetValue(identifier.Identifier.Text, out var source))
            {
                if (source.IsArray)
                {
                    deps.AddRange(source.Indices);
                }
                else
                {
                    deps.Add(source.Index);
                }
            }
        }

        return deps;
    }

    /// <summary>
    /// How each in-scope step-output local is spelled inside an emitted call: a scalar becomes
    /// <c>__inputs.Get&lt;T&gt;(i)</c>, an array-bound group becomes <c>new T[] { … }</c> over its
    /// elements' gets.
    /// </summary>
    private Dictionary<string, ExpressionSyntax> BuildReplacements()
    {
        var map = new Dictionary<string, ExpressionSyntax>();
        foreach (var pair in _vars)
        {
            if (pair.Value.IsArray)
            {
                var elementType = pair.Value.ElementType!;
                map[pair.Key] = ArrayCreationExpression(
                        ArrayType(elementType).WithRankSpecifiers(SingletonList(
                            ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(
                                OmittedArraySizeExpression())))))
                    .WithInitializer(InitializerExpression(
                        SyntaxKind.ArrayInitializerExpression,
                        SeparatedList<ExpressionSyntax>(
                            pair.Value.Indices.Select(i => InputsGet(elementType, i)))));
            }
            else
            {
                var producer = _steps.First(s => s.Index == pair.Value.Index);
                map[pair.Key] = InputsGet(producer.ResultType, pair.Value.Index);
            }
        }

        return map;
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
    /// arguments rewritten in terms of <c>__inputs</c>, and <c>__ctx</c> appended when the method
    /// takes a trailing <c>ScenarioContext</c> the scenario left out. Outer trivia is dropped — the
    /// call is re-hosted inside a lambda, and the emitter formats the whole file.
    /// </summary>
    private static InvocationExpressionSyntax BuildCall(
        InvocationExpressionSyntax invocation,
        Dictionary<string, ExpressionSyntax> replacements,
        bool appendCtx)
    {
        var rewriter = new IdentifierReplacer(replacements);
        var arguments = invocation.ArgumentList.Arguments
            .Select(a => (ArgumentSyntax)rewriter.Visit(a))
            .ToList();

        if (appendCtx)
        {
            arguments.Add(Argument(IdentifierName("__ctx")));
        }

        return invocation
            .WithArgumentList(ArgumentList(SeparatedList(arguments)))
            .WithoutTrivia();
    }

    /// <summary>
    /// Lowers the method's resource role attributes into <see cref="ResourceRoleClaim"/>s: one per
    /// role-bearing parameter (its rewritten argument expression), then one for a return role when the
    /// step yields a value (using <c>__r</c>). Argument expressions are rewritten with the SAME
    /// replacements as the call text, so step-output locals become <c>__inputs.Get&lt;…&gt;(i)</c>.
    /// Empty when the method declares no roles ⇒ the emitter inserts nothing.
    /// </summary>
    private static List<ResourceRoleClaim> BuildResourceClaims(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        bool hasResult,
        Dictionary<string, ExpressionSyntax> replacements)
    {
        var claims = new List<ResourceRoleClaim>();
        var rewriter = new IdentifierReplacer(replacements);
        var arguments = invocation.ArgumentList.Arguments;

        // Emits the lineage relations a producing subject declares via [Created]/[Loaded]/[Edited]'s
        // References/Consumes: one Reference/Consume claim per resolvable target, the subject expression
        // riding along (the runtime records subject→target). Emitted BEFORE the subject's own role claim,
        // so effect order stays target-lineage-then-subject (e.g. Reference, Consume, then Create).
        void EmitLineage((System.Collections.Immutable.ImmutableArray<string> References,
            System.Collections.Immutable.ImmutableArray<string> Consumes) lineage, ExpressionSyntax subjectExpression)
        {
            foreach (var target in lineage.References)
            {
                if (ResolveTargetExpression(target, method, arguments, rewriter) is { } expr)
                {
                    claims.Add(new ResourceRoleClaim("Reference", expr, IsReturn: false) { SubjectExpressions = [subjectExpression] });
                }
            }

            foreach (var target in lineage.Consumes)
            {
                if (ResolveTargetExpression(target, method, arguments, rewriter) is { } expr)
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

            var argument = FindArgument(arguments, parameter.Name, p);
            if (argument is null)
            {
                // No supplied argument. The trailing-ScenarioContext case is correct (nothing to claim).
                // The omitted-optional case (a declared role on a defaulted param that the call left out)
                // is a deliberate, known gap: the role silently vanishes. Diagnosing it is intentionally
                // deferred to RAUN009 (Task 9); a future reader should not treat this skip as a bug.
                continue;
            }

            var expression = ((ExpressionSyntax)rewriter.Visit(argument.Expression)).WithoutTrivia();
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
    /// Maps a producer's lineage target name to an instance expression: <c>Subject.Return</c> ⇒
    /// <c>__r</c>; a parameter name ⇒ that parameter's rewritten argument expression. Null when the
    /// name resolves to no supplied argument (the analyzer reports it as RAUN010).
    /// </summary>
    private static ExpressionSyntax? ResolveTargetExpression(
        string name,
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IdentifierReplacer rewriter)
    {
        if (name == AttributeReader.ReturnSubject)
        {
            return ReturnValue;
        }

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].Name != name)
            {
                continue;
            }

            var arg = FindArgument(arguments, name, i);
            return arg is null ? null : ((ExpressionSyntax)rewriter.Visit(arg.Expression)).WithoutTrivia();
        }

        return null;
    }

    /// <summary>
    /// Finds the argument bound to the parameter at <paramref name="position"/>: a named argument
    /// (<c>name: value</c>) matching <paramref name="parameterName"/> if present, else the positional
    /// argument at that index. Null when neither exists (e.g. an omitted optional parameter).
    /// </summary>
    internal static ArgumentSyntax? FindArgument(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        string parameterName,
        int position)
    {
        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.Text == parameterName)
            {
                return argument;
            }
        }

        return position < arguments.Count && arguments[position].NameColon is null
            ? arguments[position]
            : null;
    }

    // The emitter dedupes the merged using set, so no need to dedupe here. Trivia is stripped: the
    // scenario file's comments and formatting have no business in the generated one.
    private IEnumerable<UsingDirectiveSyntax> CollectUsings()
        => _syntax.SyntaxTree.GetCompilationUnitRoot().Usings.Select(u => u.WithoutTrivia());

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
