using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Diagnostics;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Raun.Generator.Syntax.Literals;

namespace Raun.Generator.Lowering;

/// <summary>
/// The one reader of a <c>[Scenario]</c> body. It walks the statements in source order, recognizes
/// awaited Given/When/Then calls (singly, in tuples, in arrays, or via a constant LINQ
/// <c>.ToArray()</c>), and records each step's dataflow + source-order dependencies, output binding,
/// display name, and lowered invocation.
/// </summary>
/// <remarks>
/// It is also the only judge of what a scenario body may contain. It does not stop at the first
/// problem: it reports every one it finds (RAUN001–007, RAUN011, RAUN013, RAUN017, RAUN019–021) and keeps walking, so a
/// build names them all. The outcome is a scenario or its diagnostics — never both, never neither —
/// and the generator reports what it gets. Nothing else walks a scenario body, so nothing can
/// disagree with it.
/// </remarks>
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

    // Everything wrong with the body, in the order found; a set alongside so a LINQ unroll, which
    // resolves its one body once per element, reports each problem once.
    private readonly List<ScenarioDiagnostic> _diagnostics = [];
    private readonly HashSet<ScenarioDiagnostic> _reported = [];

    // Indices introduced by the previous top-level statement (source-order barrier / join target).
    private List<int> _prevFrontier = [];

    // Ordering-only predecessors the NEXT statement must wait for: the last steps of every arm of the
    // `if` that just closed. DependsOn cannot carry them (an arm may be not-taken, and DependsOn would
    // cascade that), so they ride on WaitsFor. Consumed and cleared by the statement that follows.
    private List<int> _pendingWaits = [];

    // Guards accumulated by the enclosing if/else arms; every step created inherits a snapshot.
    private readonly List<ParsedGuard> _guards = [];

    private int _nextIndex;
    private string _scenarioId = "";

    // The scenario's world when its class derives from Raun.Scenarios<TWorld>, else null; and the
    // index of the Setup node that creates it (every step depends on it), -1 when there is none.
    private ITypeSymbol? _world;
    private int _setupIndex = -1;

    private readonly List<ParsedStep> _steps = [];

    private ScenarioParser(SemanticModel model, IMethodSymbol method, MethodDeclarationSyntax syntax)
    {
        _model = model;
        _method = method;
        _syntax = syntax;
    }

    /// <summary>What a step-output local holds. Three distinct shapes, so nothing can read a step
    /// index off a local that has none.</summary>
    private abstract record VarSource
    {
        /// <summary>The step(s) a read of this local depends on.</summary>
        public abstract IEnumerable<int> Producers { get; }
    }

    /// <summary>One step's result.</summary>
    private sealed record StepOutput(int Index) : VarSource
    {
        public override IEnumerable<int> Producers => [Index];
    }

    /// <summary>An array group's results, one step per element.</summary>
    private sealed record GroupOutput(int[] Indices, TypeSyntax ElementType) : VarSource
    {
        public override IEnumerable<int> Producers => Indices;
    }

    /// <summary>A pattern variable: element <paramref name="Element"/> of a case node's result
    /// (the value itself when the case binds one variable).</summary>
    private sealed record CaseVariable(int Index, int Element, int Count, TypeSyntax Type) : VarSource
    {
        public override IEnumerable<int> Producers => [Index];
    }

    /// <summary>
    /// A local a failed statement declared or assigned. It still counts as a step output, so its
    /// readers are not reported a second time for one mistake — but no step stands behind it, and the
    /// scenario it belongs to is never generated.
    /// </summary>
    private sealed record FailedOutput : VarSource
    {
        public static readonly FailedOutput Instance = new();

        public override IEnumerable<int> Producers => [];
    }

    /// <summary>A LINQ unroll's loop variable and its value for the element being built.</summary>
    private readonly record struct LoopElement(IParameterSymbol Variable, int Value);

    /// <summary>
    /// A step call resolved and its arguments lowered: everything about a step that does not depend on
    /// where it sits in the graph. <c>Receiver</c> is what the generated code calls the step on — the
    /// scenario's instance of its step class, reached through the Setup node's scope; <c>KeyPrefix</c>
    /// is the group path in front of the operation in the step's identity (<c>Patients.</c> for
    /// <c>Given.Patients.Exists(…)</c>, empty for a call on a phase itself).
    /// </summary>
    private sealed record StepCall(
        InvocationExpressionSyntax Invocation,
        MemberAccessExpressionSyntax Member,
        string Phase,
        IMethodSymbol Method,
        ITypeSymbol? ResultType,
        LoweredCall Lowering,
        LoopElement? Loop,
        ExpressionSyntax Receiver,
        string KeyPrefix)
    {

        public string Operation => Member.Name.Identifier.Text;

        public List<ParallelAccess> Accesses()
            => ParallelAccess.Of(Method, Operation, Invocation.ArgumentList.Arguments, Lowering);
    }

    /// <summary>Lowers one scenario, or reports why it cannot be.</summary>
    public static ParseOutcome Lower(SemanticModel model, IMethodSymbol method, MethodDeclarationSyntax syntax)
        => new ScenarioParser(model, method, syntax).Run();

    private ParseOutcome Run()
    {
        var scenario = LowerBody();
        return _diagnostics.Count == 0
            ? ParseOutcome.Lowered(scenario!)
            : ParseOutcome.Refused(_diagnostics.ToArray());
    }

    private string MethodFullName => _method.ContainingType.ToDisplayString(SymbolHelpers.NoGlobal) + "." + _method.Name;

    private ParsedScenario? LowerBody()
    {
        if (!_method.IsAsync || !SymbolHelpers.IsVoidTaskLike(_method.ReturnType))
        {
            Report(Descriptors.MustBeAsyncTask, _syntax.Identifier.GetLocation(), _method.Name);
        }

        if (_syntax.Body is null)
        {
            Refuse(_syntax.Identifier, "Its body must be a block of statements, not an expression");
            return null;
        }

        var methodFullName = MethodFullName;
        _scenarioId = GenStableId.ForScenario(methodFullName);

        _world = SymbolHelpers.WorldOfScenarios(_method.ContainingType);
        if (_world is null)
        {
            // Nothing in the body can be followed without a world, so the one diagnostic is this one.
            Report(
                Descriptors.ScenarioClassShape,
                _syntax.Identifier.GetLocation(),
                methodFullName,
                "is declared in '" + _method.ContainingType.Name + "', which does not derive from Scenarios<TWorld>; declare it in a class that does (Scenarios<NoWorld> for a suite with no state)");
            return null;
        }

        if (_method.IsStatic)
        {
            Report(
                Descriptors.ScenarioClassShape,
                _syntax.Identifier.GetLocation(),
                methodFullName,
                "is static; make it an instance method so it can use its class's phases");
        }

        AddSetup(_world);

        foreach (var statement in _syntax.Body.Statements)
        {
            ParseStatement(statement);
        }

        if (_diagnostics.Count > 0)
        {
            return null;
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
            DeclaringType = TypeSyntaxFactory.From(_method.ContainingType),
            MethodName = _method.Name,
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

    private void Report(DiagnosticDescriptor descriptor, Location location, params string[] arguments)
    {
        var lines = location.GetLineSpan();
        var diagnostic = new ScenarioDiagnostic(
            descriptor.Id,
            arguments,
            lines.Path,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            new SourceSpan(
                lines.Path,
                lines.StartLinePosition.Line, lines.StartLinePosition.Character,
                lines.EndLinePosition.Line, lines.EndLinePosition.Character));

        if (_reported.Add(diagnostic))
        {
            _diagnostics.Add(diagnostic);
        }
    }

    private void Report(DiagnosticDescriptor descriptor, SyntaxNode node, params string[] arguments)
        => Report(descriptor, node.GetLocation(), arguments);

    /// <summary>RAUN017: not generated, for a reason no more specific rule names.</summary>
    private void Refuse(SyntaxNode node, string reason)
        => Report(Descriptors.ScenarioNotGenerated, node, MethodFullName, reason);

    private void Refuse(SyntaxToken token, string reason)
        => Report(Descriptors.ScenarioNotGenerated, token.GetLocation(), MethodFullName, reason);

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

    /// <summary>
    /// Lowers one statement, reporting what is wrong with it. True when it lowered cleanly; either way
    /// the walk goes on to the next statement.
    /// </summary>
    private bool ParseStatement(StatementSyntax statement)
    {
        switch (statement)
        {
            case EmptyStatementSyntax:
                return true;

            case LocalDeclarationStatementSyntax local:
                return ParseLocalDeclaration(local);

            case ExpressionStatementSyntax expression:
                return ParseExpressionStatement(expression);

            case IfStatementSyntax ifStatement:
                return ParseIf(ifStatement);

            case BlockSyntax block:
                return ParseBlock(block);

            case ForStatementSyntax or ForEachStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax
                or TryStatementSyntax or UsingStatementSyntax or LockStatementSyntax
                or GotoStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax
                or ThrowStatementSyntax or YieldStatementSyntax or LabeledStatementSyntax
                or FixedStatementSyntax or CheckedStatementSyntax or UnsafeStatementSyntax
                or LocalFunctionStatementSyntax or ReturnStatementSyntax:
                Report(Descriptors.UnsupportedControlFlow, statement);
                return false;

            default:
                Report(Descriptors.UnsupportedStatement, statement);
                return false;
        }
    }

    private bool ParseBlock(BlockSyntax block)
    {
        var ok = true;
        foreach (var statement in block.Statements)
        {
            ok &= ParseStatement(statement);
        }

        return ok;
    }

    /// <summary>
    /// Lowers <c>if (await Given.C(...)) A else B</c>. The condition is an ordinary node; each arm is
    /// walked with an extra guard pushed. Locals defined differently by the two arms become phi
    /// (merge) nodes at the closing brace — a definition map diff, which is all SSA needs when the
    /// control flow is structured (every merge point IS the closing brace).
    /// </summary>
    private bool ParseIf(IfStatementSyntax statement)
    {
        var parsed = ParseCondition(statement.Condition);

        // The arms are walked even under a broken condition, so their own problems are reported too.
        var conditionIndex = parsed?.Step.Index ?? -1;
        var parentVars = new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default);

        // A pattern's variables are step outputs in the arm C#'s definite assignment puts them in:
        // arm 0 for a positive pattern, the else for a negated one (an odd number of `not`s around it,
        // through parentheses).
        IReadOnlyDictionary<ILocalSymbol, VarSource>? bind0 = null, bind1 = null;
        if (parsed is { Branch.Form: BranchForm.Pattern } p && p.PatternVariables.Count > 0)
        {
            var negated = IsNegated(p.Branch.Pattern!);
            var vIs = IsPatternExpression(IdentifierName("__v"), p.Branch.Pattern!);
            var match = negated
                ? PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, ParenthesizedExpression(vIs))
                : (ExpressionSyntax)vIs;
            var arm = negated ? 1 : 0;
            var binding = AddCaseNode(conditionIndex, arm, p.Step.ResultType, match, p.PatternVariables, p.Branch.ArmLabels[arm]);
            if (negated)
            {
                bind1 = binding;
            }
            else
            {
                bind0 = binding;
            }
        }
        else if (parsed is null)
        {
            // A refused condition's variables were reported with it: in both arms they are failed
            // outputs, so their readers are not reported a second time for the one mistake.
            bind0 = bind1 = PatternVariables(statement.Condition)
                .ToDictionary<ILocalSymbol, ILocalSymbol, VarSource>(local => local, _ => FailedOutput.Instance, SymbolEqualityComparer.Default);
        }

        var arms = new List<ArmWalk>
        {
            WalkArm([statement.Statement], conditionIndex, arm: 0, parentVars, bind0),
            WalkArm(statement.Else is { } elseClause ? [elseClause.Statement] : [], conditionIndex, arm: 1, parentVars, bind1),
        };

        return Rejoin(statement, conditionIndex, parentVars, arms, ok: parsed is not null);
    }

    /// <summary>
    /// RAUN011: the condition is an awaited step call whose result can drive a C# <c>if</c>
    /// (<c>bool</c>, an implicit conversion to it, or <c>operator true</c>), or an awaited step call
    /// tested by an <c>is</c> pattern. Returns the condition step, its branch, and the variables the
    /// pattern declares, or null when there is no step to guard on.
    /// </summary>
    private (ParsedStep Step, ParsedBranch Branch, IReadOnlyList<ILocalSymbol> PatternVariables)? ParseCondition(ExpressionSyntax condition)
    {
        // `await S is P`, or `await S is T`, which C# reads as a type test rather than a pattern.
        var (patternCall, tested) = condition switch
        {
            IsPatternExpressionSyntax { Expression: AwaitExpressionSyntax { Expression: InvocationExpressionSyntax c } } isPattern
                => (c, (SyntaxNode)isPattern.Pattern),
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression, Left: AwaitExpressionSyntax { Expression: InvocationExpressionSyntax c }, Right: TypeSyntax type }
                => (c, type),
            _ => (null, null),
        };

        if (patternCall is not null && tested is not null)
        {
            var testedCall = ResolveCall(patternCall, Descriptors.InvalidCondition);
            if (testedCall is null || testedCall.ResultType is null)
            {
                if (testedCall is not null)
                {
                    Report(Descriptors.InvalidCondition, condition);
                }

                return null;
            }

            if (LowerPattern(tested, condition) is not { } pattern)
            {
                return null;
            }

            var patternStep = BuildStep(testedCall, groupId: null, _prevFrontier);
            var patternBranch = new ParsedBranch
            {
                Form = BranchForm.Pattern,
                ValueType = patternStep.ResultType,
                Pattern = pattern,
                ArmLabels = ["is " + tested.ToString(), "else"],
            };
            MarkAsCondition(patternStep, patternBranch);
            return (patternStep, patternBranch, PatternVariables(tested));
        }

        if (condition is not AwaitExpressionSyntax { Expression: InvocationExpressionSyntax invocation })
        {
            Report(Descriptors.InvalidCondition, condition);
            return null;
        }

        var call = ResolveCall(invocation, Descriptors.InvalidCondition);
        if (call is null)
        {
            return null;
        }

        if (call.ResultType is null || !SymbolHelpers.IsUsableAsCondition(call.ResultType, _model.Compilation))
        {
            Report(Descriptors.InvalidCondition, condition);
            return null;
        }

        var step = BuildStep(call, groupId: null, _prevFrontier);
        var branch = new ParsedBranch
        {
            Form = BranchForm.Truth,
            ValueType = step.ResultType,
            ArmLabels = ["if", "else"],
        };
        MarkAsCondition(step, branch);
        return (step, branch, []);
    }

    /// <summary>
    /// The pattern a condition tests, re-hosted like a step argument, or null after reporting why it
    /// cannot be. <c>is X</c> with a bare name parses as a type test whatever X is: a type becomes a
    /// type pattern, and anything else (a constant: <c>is Limit</c>, an enum member brought in by
    /// <c>using static</c>) a constant pattern, which is how C# binds it.
    /// </summary>
    private PatternSyntax? LowerPattern(SyntaxNode tested, SyntaxNode condition)
    {
        IReadOnlyList<ArgumentViolation> violations;
        PatternSyntax pattern;
        if (tested is TypeSyntax type)
        {
            switch (_model.GetSymbolInfo(type).Symbol)
            {
                case ITypeSymbol:
                    var loweredType = ArgumentLowering.Lower(_model, type, StepValueFor(loop: null));
                    (violations, pattern) = (loweredType.Violations, TypePattern(loweredType.Node));
                    break;
                case { }:
                    var loweredValue = ArgumentLowering.Lower<ExpressionSyntax>(_model, type, StepValueFor(loop: null));
                    (violations, pattern) = (loweredValue.Violations, ConstantPattern(loweredValue.Node));
                    break;
                default:
                    Report(Descriptors.InvalidCondition, condition);
                    return null;
            }
        }
        else
        {
            var lowered = ArgumentLowering.Lower(_model, (PatternSyntax)tested, StepValueFor(loop: null));
            (violations, pattern) = (lowered.Violations, lowered.Node);
        }

        foreach (var violation in violations)
        {
            Report(Descriptors.InvalidArgument, violation.Node, violation.Subject, violation.Reason);
        }

        return violations.Count == 0 ? pattern : null;
    }

    /// <summary>True when a pattern is negated — an odd number of <c>not</c>s around it, through
    /// parentheses — so its variables are definitely assigned when it does NOT match.</summary>
    private static bool IsNegated(PatternSyntax pattern)
    {
        var negated = false;
        while (true)
        {
            switch (pattern)
            {
                case ParenthesizedPatternSyntax parenthesized:
                    pattern = parenthesized.Pattern;
                    continue;
                case UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern } not:
                    negated = !negated;
                    pattern = not.Pattern;
                    continue;
                default:
                    return negated;
            }
        }
    }

    /// <summary>The variables a pattern or condition declares (<c>Rejected r</c>,
    /// <c>{ Total: var t }</c>), in order.</summary>
    private List<ILocalSymbol> PatternVariables(SyntaxNode node)
        => node.DescendantNodesAndSelf()
            .OfType<SingleVariableDesignationSyntax>()
            .Select(d => _model.GetDeclaredSymbol(d))
            .OfType<ILocalSymbol>()
            .ToList();

    /// <summary>
    /// Adds the hidden case node of one arm — guarded on that arm, depending on the condition — which
    /// re-matches the arm's pattern on the recorded value and returns its variables, and returns the
    /// arm's bindings of those variables to it. Null when the pattern declares none.
    /// </summary>
    private Dictionary<ILocalSymbol, VarSource>? AddCaseNode(
        int conditionIndex, int arm, TypeSyntax valueType, ExpressionSyntax match, IReadOnlyList<ILocalSymbol> variables, string label)
    {
        if (variables.Count == 0 || conditionIndex < 0)
        {
            return null;
        }

        var resultType = variables.Count == 1
            ? TypeSyntaxFactory.From(variables[0].Type)
            : TupleType(SeparatedList(variables.Select(v => TupleElement(TypeSyntaxFactory.From(v.Type)))));
        ExpressionSyntax result = variables.Count == 1
            ? IdentifierName(variables[0].Name)
            : TupleExpression(SeparatedList(variables.Select(v => Argument(IdentifierName(v.Name)))));

        var index = _nextIndex++;
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "case:" + conditionIndex + ":" + arm),
            Phase = _steps.First(s => s.Index == conditionIndex).Phase,
            OperationName = "Case",
            HasResult = true,
            ResultType = resultType,
            DisplayNameTemplate = "«" + label + "»",
            IsSynthetic = true,
            Guards = [.. _guards, new ParsedGuard(conditionIndex, arm)],
            DependsOn = [conditionIndex],
            CaseBinding = new ParsedCaseBinding
            {
                ConditionIndex = conditionIndex,
                ValueType = valueType,
                Match = match,
                Result = result,
            },
        });

        var bindings = new Dictionary<ILocalSymbol, VarSource>(SymbolEqualityComparer.Default);
        for (var i = 0; i < variables.Count; i++)
        {
            bindings[variables[i]] = new CaseVariable(index, i, variables.Count, resultType);
        }

        return bindings;
    }

    /// <summary>Closes a top-level statement: the next statement joins on <paramref name="frontier"/>
    /// and additionally waits for <paramref name="waits"/> (ordering only).</summary>
    private void Advance(List<int> frontier, List<int>? waits = null)
    {
        _prevFrontier = frontier;
        _pendingWaits = waits ?? [];
    }

    private void MarkAsCondition(ParsedStep condition, ParsedBranch branch)
    {
        var position = _steps.FindIndex(s => s.Index == condition.Index);
        _steps[position] = _steps[position] with { Branch = branch };
    }

    /// <summary>One arm's walk result.</summary>
    private readonly record struct ArmWalk(Dictionary<ILocalSymbol, VarSource> Vars, List<int> Waits, bool Ok);

    /// <summary>Walks one arm under Guard(conditionIndex, arm), with <paramref name="bind"/> applied
    /// to its definition map first (an arm's pattern variables).</summary>
    private ArmWalk WalkArm(
        IEnumerable<StatementSyntax> body,
        int conditionIndex,
        int arm,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyDictionary<ILocalSymbol, VarSource>? bind = null)
    {
        var savedFrontier = _prevFrontier;
        var savedWaits = _pendingWaits;
        _vars.Clear();
        foreach (var pair in parentVars)
        {
            _vars[pair.Key] = pair.Value;
        }

        if (bind is not null)
        {
            foreach (var pair in bind)
            {
                _vars[pair.Key] = pair.Value;
            }
        }

        _guards.Add(new ParsedGuard(conditionIndex, arm));
        _prevFrontier = conditionIndex >= 0 ? [conditionIndex] : [];
        _pendingWaits = [];
        var ok = true;
        foreach (var statement in body)
        {
            ok &= ParseStatement(statement);
        }

        _guards.RemoveAt(_guards.Count - 1);

        var tail = new List<int>(_prevFrontier);
        tail.AddRange(_pendingWaits);
        _prevFrontier = savedFrontier;
        _pendingWaits = savedWaits;

        return new ArmWalk(new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default), tail, ok);
    }

    /// <summary>Rejoins after a branch: one N-way merge per local the arms define differently, then
    /// Advance. Returns false when a merge was refused.</summary>
    private bool Rejoin(
        SyntaxNode branch,
        int conditionIndex,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyList<ArmWalk> arms,
        bool ok)
    {
        ok &= arms.All(a => a.Ok);

        _vars.Clear();
        foreach (var pair in parentVars)
        {
            _vars[pair.Key] = pair.Value;
        }

        var frontier = new List<int>();
        foreach (var local in DifferingLocals(parentVars, arms))
        {
            // Nothing to merge when the branch is broken, or when a failed statement defined the
            // local: the local stays failed, already reported where it went wrong.
            var definitions = arms.Select(a => a.Vars.TryGetValue(local, out var s) ? s : null)
                .Append(parentVars.TryGetValue(local, out var p) ? p : null);
            if (!ok || definitions.Any(source => source is FailedOutput))
            {
                _vars[local] = FailedOutput.Instance;
                continue;
            }

            var mergeIndex = InsertMerge(local, conditionIndex, parentVars, arms, out var unmergeable);
            if (mergeIndex < 0)
            {
                Refuse(branch, unmergeable is CaseVariable
                    ? "'" + local.Name + "' is one of several variables bound by one pattern, which cannot be merged across the arms of a branch; bind it with a pattern of its own"
                    : "'" + local.Name + "' holds an array group, which cannot be merged across the arms of a branch; bind each arm's group to its own local");
                _vars[local] = FailedOutput.Instance;
                ok = false;
                continue;
            }

            frontier.Add(mergeIndex);
        }

        if (conditionIndex < 0)
        {
            return false;
        }

        // An empty arm's frontier is the condition itself, which the next statement already depends on.
        var waits = new SortedSet<int>(arms.SelectMany(a => a.Waits));
        waits.Remove(conditionIndex);

        // A following statement must never DEPEND on an arm's node (DependsOn is all-of and an arm may
        // not run); it joins on the merges, or on the condition when there are none. It must still
        // WAIT for every arm's last steps, or it would run concurrently with the inside of the branch —
        // that is what WaitsFor carries, and a not-taken arm does not cascade through it.
        Advance(frontier.Count > 0 ? frontier : [conditionIndex], [.. waits]);
        return ok;
    }

    /// <summary>Locals whose definition differs between the arms (or between an arm and the parent) —
    /// exactly the set that needs a phi. A local declared inside one arm is branch-local (absent from
    /// the parent and from the other arms) and C# scoping forbids its later use, so it is dropped; a
    /// local declared before the branch without a value and assigned in EVERY arm is the ordinary
    /// phi. Ordered by name, then declaration position, so merges are inserted identically every run.</summary>
    private static IEnumerable<ILocalSymbol> DifferingLocals(
        Dictionary<ILocalSymbol, VarSource> parentVars, IReadOnlyList<ArmWalk> arms)
    {
        var locals = arms.SelectMany(a => a.Vars.Keys)
            .Distinct<ILocalSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(l => l.Name, System.StringComparer.Ordinal)
            .ThenBy(l => l.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0);

        foreach (var local in locals)
        {
            var inParent = parentVars.TryGetValue(local, out var parentDef);
            if (!inParent && !arms.All(a => a.Vars.ContainsKey(local)))
            {
                continue;
            }

            var defs = arms.Select(a => a.Vars.TryGetValue(local, out var s) ? s : parentDef).ToList();
            if (defs.Distinct().Count() > 1)
            {
                yield return local;
            }
        }
    }

    /// <summary>
    /// Inserts the phi for one local: a synthetic merge with one source per arm — the arm's own
    /// definition, or, when the arm left the local alone, a PASS-THROUGH aliasing the parent
    /// definition, guarded on that arm. Every source is guarded on a different arm of one condition,
    /// so they are mutually exclusive, as <c>ScenarioDefinition.Validate</c> requires. Typed as the
    /// LOCAL, not as any one arm's producer, since arms may produce different subtypes. A source must be
    /// one node's value (<see cref="ValueNode"/>): otherwise nothing is inserted, -1 is returned, and
    /// <paramref name="unmergeable"/> is the definition the caller refuses the shape over.
    /// </summary>
    private int InsertMerge(
        ILocalSymbol local,
        int conditionIndex,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyList<ArmWalk> arms,
        out VarSource? unmergeable)
    {
        // Each arm's source: its own definition, or the parent's through a pass-through. All are
        // checked before anything is inserted, so a refused merge leaves no stray node behind.
        var picks = new List<(int Node, bool PassThrough)>();
        for (var arm = 0; arm < arms.Count; arm++)
        {
            var inArm = arms[arm].Vars.TryGetValue(local, out var armSource);
            var inParent = parentVars.TryGetValue(local, out var definedBefore);
            var passThrough = !(inArm && !(inParent && Equals(armSource, definedBefore)));
            var definition = passThrough ? definedBefore : armSource;
            if (ValueNode(definition) is not { } node)
            {
                unmergeable = definition;
                return -1;
            }

            picks.Add((node, passThrough));
        }

        unmergeable = null;
        var sources = new List<int>();
        for (var arm = 0; arm < picks.Count; arm++)
        {
            sources.Add(picks[arm].PassThrough
                ? InsertPassThrough(local, conditionIndex, arm, picks[arm].Node)
                : picks[arm].Node);
        }

        var phase = _steps.First(s => s.Index == sources[0]).Phase;
        var index = _nextIndex++;
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "merge:" + local.Name + ":" + index),
            Phase = phase,
            OperationName = "Merge",
            HasResult = true,
            ResultType = TypeSyntaxFactory.From(local.Type),
            DisplayNameTemplate = "«merge " + local.Name + "»",
            MergeSources = sources,
            IsSynthetic = true,
            Guards = [.. _guards],
            DependsOn = [],
        });

        _vars[local] = new StepOutput(index);
        return index;
    }

    /// <summary>The node whose result IS the local's value — a step's output, or the case node of a
    /// pattern that binds this one variable — or null when no single node's result is (an array group,
    /// one of several pattern variables, a missing definition).</summary>
    private static int? ValueNode(VarSource? source) => source switch
    {
        StepOutput step => step.Index,
        CaseVariable { Count: 1 } variable => variable.Index,
        _ => null,
    };

    /// <summary>
    /// Stands in for an arm that did not redefine the local: a synthetic node aliasing the parent
    /// definition, guarded on the arm it OCCUPIES, so a merge's sources stay mutually exclusive and
    /// the parent value flows through when that arm runs.
    /// </summary>
    private int InsertPassThrough(ILocalSymbol local, int conditionIndex, int arm, int parentDef)
    {
        var producer = _steps.First(s => s.Index == parentDef);
        var index = _nextIndex++;
        var guards = new List<ParsedGuard>(_guards) { new(conditionIndex, arm) };
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "phi:" + local.Name + ":" + index),
            Phase = producer.Phase,
            OperationName = "Unchanged",
            HasResult = true,
            ResultType = TypeSyntaxFactory.From(local.Type),
            DisplayNameTemplate = "«" + local.Name + " unchanged»",
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
            Report(Descriptors.UnsupportedStatement, local);
            Fail(variables.Select(v => _model.GetDeclaredSymbol(v)).OfType<ILocalSymbol>());
            return false;
        }

        // `Appointment appointment;` — a declaration with no initializer produces no step. It only
        // introduces the name; the definition arrives from an assignment, typically one per `if` arm,
        // and the definition-map diff turns those into a phi at the closing brace.
        if (variables[0].Initializer is null)
        {
            return true;
        }

        if (_model.GetDeclaredSymbol(variables[0]) is not ILocalSymbol declared)
        {
            Refuse(local, "The declared local does not resolve");
            return false;
        }

        if (variables[0].Initializer?.Value is not AwaitExpressionSyntax await)
        {
            Report(Descriptors.UnsupportedStatement, local);
            Fail([declared]);
            return false;
        }

        return ParseBinding(await.Expression, Binding.Single(declared));
    }

    private bool ParseExpressionStatement(ExpressionStatementSyntax statement)
    {
        switch (statement.Expression)
        {
            case AwaitExpressionSyntax bareAwait:
                return ParseAwaited(bareAwait.Expression, binding: null);

            case AssignmentExpressionSyntax { Right: AwaitExpressionSyntax await } assignment:
                // An assignment binds locals; one that binds nothing (a field, a property) has nowhere
                // to put the value, and lowering it as a bare step would drop the write without a word.
                if (Binding.FromAssignment(assignment.Left, _model) is { } binding)
                {
                    return ParseBinding(await.Expression, binding);
                }

                ParseAwaited(await.Expression, binding: null);
                Report(Descriptors.UnsupportedStatement, assignment.Left);
                return false;

            default:
                Report(Descriptors.UnsupportedStatement, statement);
                if (statement.Expression is AssignmentExpressionSyntax other
                    && Binding.FromAssignment(other.Left, _model) is { } assigned)
                {
                    Fail(assigned.Locals);
                }

                return false;
        }
    }

    /// <summary>Marks locals a failed statement declared or assigned as failed step outputs.</summary>
    private void Fail(IEnumerable<ILocalSymbol> locals)
    {
        foreach (var local in locals)
        {
            _vars[local] = FailedOutput.Instance;
        }
    }

    /// <summary>An awaited statement that binds locals; when it fails, they are failed outputs.</summary>
    private bool ParseBinding(ExpressionSyntax awaited, Binding binding)
    {
        if (ParseAwaited(awaited, binding))
        {
            return true;
        }

        Fail(binding.Locals);
        return false;
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
                return ParseArray(array.Initializer, awaited, binding);
            case ImplicitArrayCreationExpressionSyntax array:
                return ParseArray(array.Initializer, awaited, binding);
            default:
                Report(Descriptors.UnsupportedStatement, awaited);
                return false;
        }
    }

    private bool ParseSingleCall(InvocationExpressionSyntax invocation, Binding? binding)
    {
        if (binding is { Kind: BindingKind.Tuple })
        {
            Refuse(invocation, "A single step's result cannot be deconstructed; bind it to one local and use its members");
            return false;
        }

        if (ResolveCall(invocation, Descriptors.NotADslCall) is not { } call)
        {
            return false;
        }

        var step = BuildStep(call, groupId: null, _prevFrontier);
        if (binding is { Kind: BindingKind.Single })
        {
            _vars[binding.Locals[0]] = new StepOutput(step.Index);
        }

        Advance([step.Index]);
        return true;
    }

    private bool ParseTuple(TupleExpressionSyntax tuple, Binding? binding)
    {
        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        var accesses = new List<IReadOnlyList<ParallelAccess>>();
        var locals = binding?.Locals;
        var ok = true;

        for (var i = 0; i < tuple.Arguments.Count; i++)
        {
            if (ResolveGroupElement(tuple.Arguments[i].Expression) is not { } call)
            {
                ok = false;
                continue;
            }

            var step = BuildStep(call, groupId, _prevFrontier);
            accesses.Add(call.Accesses());
            if (locals is not null && i < locals.Count)
            {
                _vars[locals[i]] = new StepOutput(step.Index);
            }

            frontier.Add(step.Index);
        }

        ok &= CheckParallelConflicts(accesses);
        Advance(frontier);
        return ok;
    }

    private bool ParseArray(InitializerExpressionSyntax? initializer, ExpressionSyntax awaited, Binding? binding)
    {
        if (initializer is null)
        {
            Report(Descriptors.UnsupportedStatement, awaited);
            return false;
        }

        if (binding is { Kind: BindingKind.Tuple })
        {
            Refuse(awaited, "An array group binds to one local, not a deconstruction");
            return false;
        }

        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        var accesses = new List<IReadOnlyList<ParallelAccess>>();
        TypeSyntax elementType = PredefinedType(Token(SyntaxKind.ObjectKeyword));
        var ok = true;

        foreach (var element in initializer.Expressions)
        {
            if (ResolveGroupElement(element) is not { } call)
            {
                ok = false;
                continue;
            }

            var step = BuildStep(call, groupId, _prevFrontier);
            accesses.Add(call.Accesses());
            elementType = step.ResultType;
            frontier.Add(step.Index);
        }

        ok &= CheckParallelConflicts(accesses);

        if (binding is not null && ok)
        {
            _vars[binding.Locals[0]] = new GroupOutput(frontier.ToArray(), elementType);
        }

        // An empty group ran nothing, so the step after it still follows the step before it.
        if (frontier.Count > 0)
        {
            Advance(frontier);
        }

        return ok;
    }

    private bool ParseLinqArray(InvocationExpressionSyntax toArray, Binding? binding)
    {
        // Shape: Enumerable.Range(start, count).Select(i => <DSL call using i>).ToArray()
        if (toArray.Expression is not MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax selectInv }
            || selectInv.Expression is not MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax rangeInv } selectMember
            || selectMember.Name.Identifier.Text != "Select"
            || selectInv.ArgumentList.Arguments.Count != 1
            || selectInv.ArgumentList.Arguments[0].Expression is not SimpleLambdaExpressionSyntax { Body: InvocationExpressionSyntax bodyCall } lambda
            || rangeInv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Range" }
            || rangeInv.ArgumentList.Arguments.Count != 2
            || _model.GetConstantValue(rangeInv.ArgumentList.Arguments[0].Expression).Value is not int start
            || _model.GetConstantValue(rangeInv.ArgumentList.Arguments[1].Expression).Value is not int count
            || _model.GetDeclaredSymbol(lambda.Parameter) is not IParameterSymbol loopVariable)
        {
            Report(Descriptors.UnsupportedStatement, toArray);
            return false;
        }

        if (binding is { Kind: BindingKind.Tuple })
        {
            Refuse(toArray, "An array group binds to one local, not a deconstruction");
            return false;
        }

        // The body is one call written once, so it is judged once — even when the count is zero and
        // no element is ever built. Each element then resolves it with its own loop value.
        if (ResolveCall(bodyCall, Descriptors.InvalidGroupElement, new LoopElement(loopVariable, start)) is not { } first)
        {
            return false;
        }

        // Every element is a copy of the same call, so a mutating role on an outer step output
        // conflicts with itself as soon as there are two of them.
        var ok = count < 2 || CheckParallelConflicts([first.Accesses(), first.Accesses()]);

        var groupId = "g" + _nextIndex;
        var frontier = new List<int>();
        TypeSyntax elementType = PredefinedType(Token(SyntaxKind.ObjectKeyword));

        for (var k = 0; k < count; k++)
        {
            // Each element is the lambda body with the loop variable bound to that element's value.
            var call = k == 0 ? first : ResolveCall(bodyCall, Descriptors.InvalidGroupElement, new LoopElement(loopVariable, start + k))!;
            var step = BuildStep(call, groupId, _prevFrontier);
            elementType = step.ResultType;
            frontier.Add(step.Index);
        }

        if (binding is not null && ok)
        {
            _vars[binding.Locals[0]] = new GroupOutput(frontier.ToArray(), elementType);
        }

        // An empty group ran nothing, so the step after it still follows the step before it.
        if (frontier.Count > 0)
        {
            Advance(frontier);
        }

        return ok;
    }

    private static bool IsToArray(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToArray" };

    /// <summary>RAUN006: every element of a tuple or array group is a step call.</summary>
    private StepCall? ResolveGroupElement(ExpressionSyntax element)
    {
        if (element is InvocationExpressionSyntax invocation)
        {
            return ResolveCall(invocation, Descriptors.InvalidGroupElement);
        }

        Report(Descriptors.InvalidGroupElement, element);
        return null;
    }

    /// <summary>
    /// RAUN013 for one parallel group: its elements run concurrently, so two of them reaching the same
    /// step output through role-bearing parameters conflict when at least one role mutates.
    /// Concurrency inside a scenario comes ONLY from these groups — sequential statements join on the
    /// previous frontier — so the group IS the concurrency. Two different locals that resolve to one
    /// runtime identity are the scheduler's conflict ledger's job.
    /// </summary>
    private bool CheckParallelConflicts(IEnumerable<IReadOnlyList<ParallelAccess>> elements)
    {
        var ok = true;
        foreach (var (earlier, later) in ParallelAccess.Conflicts(elements))
        {
            Report(
                Descriptors.ConflictingParallelAccess,
                later.Node,
                earlier.Operation,
                later.Operation,
                later.Local.Name,
                earlier.Verb + "/" + later.Verb);
            ok = false;
        }

        return ok;
    }

    /// <summary>
    /// Resolves a DSL call and lowers its arguments, reporting everything wrong with it:
    /// <paramref name="notDsl"/> when it is not a step call (RAUN004, RAUN006 in a group,
    /// RAUN011 as a condition), RAUN007 for each argument the one argument lowering refuses, RAUN005 for
    /// a return type that is not a task. <paramref name="loop"/> binds a LINQ unroll's loop variable to
    /// the value of the element being resolved.
    /// </summary>
    private StepCall? ResolveCall(InvocationExpressionSyntax invocation, DiagnosticDescriptor notDsl, LoopElement? loop = null)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || _model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol { IsStatic: false } method
            || SymbolHelpers.WorldOfStepClass(method.ContainingType) is not { } stepWorld)
        {
            Report(notDsl, invocation);
            return null;
        }

        if (ResolveStepClassReceiver(member, method, stepWorld) is not { } resolved)
        {
            return null;
        }

        var (receiver, keyPrefix, phase) = resolved;

        var lowering = ArgumentLowering.LowerCall(_model, invocation, StepValueFor(loop));
        var ok = true;
        foreach (var violation in lowering.Violations)
        {
            Report(Descriptors.InvalidArgument, violation.Node, violation.Subject, violation.Reason);
            ok = false;
        }

        if (!SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType))
        {
            Report(Descriptors.InvalidReturnType, invocation, method.Name);
            ok = false;
        }

        return ok
            ? new StepCall(invocation, member, phase, method, resultType, lowering, loop, receiver, keyPrefix)
            : null;
    }

    /// <summary>How a scenario-level symbol that carries a step's value is spelled in lowered code — a
    /// step-output local, or <paramref name="loop"/>'s variable — and null for anything else. Step
    /// arguments and patterns share it, so they read step outputs the same way.</summary>
    private System.Func<ISymbol, ExpressionSyntax?> StepValueFor(LoopElement? loop) => symbol => symbol switch
    {
        ILocalSymbol local when _vars.TryGetValue(local, out var source)
            => source is FailedOutput ? IdentifierName(local.Name) : Spell(source),
        IParameterSymbol parameter when loop is { } element
            && SymbolEqualityComparer.Default.Equals(parameter, element.Variable) => Num(element.Value),
        _ => null,
    };

    /// <summary>
    /// The Setup node of a scenario with a world: node 0, which awaits the world's creation and hands
    /// every step its scope. It is emitted first and every step depends on it, so a failed Setup skips
    /// the whole scenario through the ordinary dependency cascade, and a run filter keeps it.
    /// </summary>
    private void AddSetup(ITypeSymbol world)
    {
        var worldType = TypeSyntaxFactory.From(world);
        _setupIndex = _nextIndex++;
        _steps.Add(new ParsedStep
        {
            Index = _setupIndex,
            StepId = GenStableId.ForStep(_scenarioId, "setup"),
            Phase = "Setup",
            OperationName = "Setup",
            HasResult = true,
            ResultType = ScopeType(worldType),
            InvokeCall = InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        Names.Global("Raun", "ScenarioScope"),
                        GenericName(Identifier("SetupAsync"))
                            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(worldType)))))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("__ctx"))))),
            DisplayNameTemplate = "Setup",
            IsSetup = true,
            DependsOn = [],
        });
    }

    /// <summary><c>global::Raun.ScenarioScope&lt;TWorld&gt;</c>.</summary>
    private static NameSyntax ScopeType(TypeSyntax world)
        => Names.Generic(Names.Global("Raun"), "ScenarioScope", world);

    /// <summary>
    /// A call to a step of a step class, <c>Given.Patients.Exists("Jane")</c>. The receiver is checked
    /// (RAUN019) but never evaluated: Raun binds a step class by its type, so the generated call is
    /// <c>__inputs.Get&lt;ScenarioScope&lt;W&gt;&gt;(setup).Steps&lt;PatientSteps&gt;().Exists("Jane")</c>
    /// whatever the properties on the way would have returned. Returns the receiver to emit and the
    /// group path for the step's identity, or null after reporting why the call cannot be lowered.
    /// </summary>
    private (ExpressionSyntax Receiver, string KeyPrefix, string Phase)? ResolveStepClassReceiver(
        MemberAccessExpressionSyntax member,
        IMethodSymbol method,
        ITypeSymbol stepWorld)
    {
        var receiverType = _model.GetTypeInfo(member.Expression).Type ?? method.ContainingType;
        var receiverText = member.Expression.WithoutTrivia().ToString();

        if (_world is not { } world || _setupIndex < 0)
        {
            // Unreachable: a scenario without a world is refused before its body is walked.
            return null;
        }

        if (!SymbolEqualityComparer.Default.Equals(stepWorld, world))
        {
            Report(Descriptors.WorldMismatch, member.Expression, receiverType.Name, stepWorld.Name, world.Name);
            return null;
        }

        var path = new List<string>();
        if (!FollowReceiver(member.Expression, path))
        {
            return null;
        }

        if (SymbolHelpers.PhaseNameOf(receiverType) is not { } phase)
        {
            Report(
                Descriptors.UnfollowableStepReceiver,
                member.Expression,
                receiverText,
                "'" + receiverType.Name + "' derives from Phase<" + stepWorld.Name + "> but no class in its base chain carries [PhaseName]; derive from Given<>, When<> or Then<>, or name the phase");
            return null;
        }

        // Scope.Steps<T>() on the Setup node's output.
        var receiver = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    InputsGet(ScopeType(TypeSyntaxFactory.From(world)), _setupIndex),
                    GenericName(Identifier("Steps"))
                        .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                            TypeSyntaxFactory.From(receiverType))))))
            .WithArgumentList(ArgumentList());

        // The first link is the phase itself (Given); what follows it is the group path.
        var keyPrefix = string.Concat(path.Skip(1).Select(name => name + "."));
        return (receiver, keyPrefix, phase);
    }

    /// <summary>
    /// RAUN019: a receiver is a chain of phase and group properties from the scenario — <c>Given</c>,
    /// <c>this.Given</c>, <c>Given.Patients</c> — and each property returns <c>Steps&lt;T&gt;()</c> of its
    /// own type. That is the whole binding contract: it is what lets Raun bind by type without ever
    /// running a property, and still call exactly the instance the property names.
    /// </summary>
    private bool FollowReceiver(ExpressionSyntax receiver, List<string> path)
    {
        switch (receiver)
        {
            case IdentifierNameSyntax identifier:
                return FollowLink(identifier, identifier, onScenario: true, path);

            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } thisAccess:
                return FollowLink(thisAccess, thisAccess.Name, onScenario: true, path);

            case MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                return FollowReceiver(access.Expression, path)
                    && FollowLink(access, access.Name, onScenario: false, path);

            case ParenthesizedExpressionSyntax parenthesized:
                return FollowReceiver(parenthesized.Expression, path);

            default:
                Report(
                    Descriptors.UnfollowableStepReceiver,
                    receiver,
                    receiver.WithoutTrivia().ToString(),
                    receiver is InvocationExpressionSyntax
                        ? "it is a method call; reach a step class through a phase or group property"
                        : "it is not a phase or group property; reach a step class through one");
                return false;
        }
    }

    /// <summary>One link of a receiver: a property of the scenario class (the phase) or of a step class
    /// (a group), whose getter returns <c>Steps&lt;T&gt;()</c> of its own type.</summary>
    private bool FollowLink(ExpressionSyntax link, SimpleNameSyntax name, bool onScenario, List<string> path)
    {
        var text = link.WithoutTrivia().ToString();
        var symbol = _model.GetSymbolInfo(name).Symbol;

        if (symbol is not IPropertySymbol property)
        {
            var what = symbol switch
            {
                ILocalSymbol => "a local",
                IParameterSymbol => "a parameter",
                IFieldSymbol => "a field",
                IMethodSymbol => "a method",
                null => "a name that does not resolve",
                _ => "not a property",
            };
            Report(
                Descriptors.UnfollowableStepReceiver,
                link,
                text,
                "it is " + what + "; a step class is reached only through phase and group properties that return Steps<T>()");
            return false;
        }

        if (property.IsStatic || property.IsIndexer)
        {
            Report(Descriptors.UnfollowableStepReceiver, link, text, "a phase or group property is a non-static, non-indexed property");
            return false;
        }

        var owner = onScenario
            ? SymbolHelpers.WorldOfScenarios(property.ContainingType)
            : SymbolHelpers.WorldOfStepClass(property.ContainingType);
        if (owner is null)
        {
            Report(
                Descriptors.UnfollowableStepReceiver,
                link,
                text,
                onScenario
                    ? "'" + property.Name + "' is not declared on a class deriving from Scenarios<>"
                    : "'" + property.Name + "' is not declared on a step class");
            return false;
        }

        if (!ReturnsStepsOfItsType(property, out var why))
        {
            Report(Descriptors.UnfollowableStepReceiver, link, text, why);
            return false;
        }

        path.Add(property.Name);
        return true;
    }

    /// <summary>
    /// True when every declaration of <paramref name="property"/> in source is
    /// <c>=&gt; Steps&lt;T&gt;()</c> (or a getter returning it) with <c>T</c> the property's type. A
    /// property from a referenced assembly has no source to read and is taken at its type.
    /// </summary>
    private bool ReturnsStepsOfItsType(IPropertySymbol property, out string why)
    {
        why = "";
        foreach (var reference in property.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not PropertyDeclarationSyntax declaration)
            {
                continue;
            }

            var returned = declaration.ExpressionBody?.Expression
                ?? declaration.AccessorList?.Accessors
                    .Where(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                    .Select(a => a.ExpressionBody?.Expression
                        ?? (a.Body is { Statements.Count: 1 } body && body.Statements[0] is ReturnStatementSyntax { Expression: { } value }
                            ? value
                            : null))
                    .FirstOrDefault();

            var model = _model.Compilation.GetSemanticModel(declaration.SyntaxTree);
            if (returned is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call
                && model.GetSymbolInfo(call).Symbol is IMethodSymbol accessor
                && SymbolHelpers.IsStepsAccessor(accessor)
                && SymbolEqualityComparer.Default.Equals(accessor.TypeArguments[0], property.Type))
            {
                continue;
            }

            why = "'" + property.Name + "' must be declared '=> Steps<" + property.Type.Name + ">()'. Raun binds a step class by its type and never runs the property, so a property that returns anything else would not be what runs";
            return false;
        }

        return true;
    }

    /// <summary>Adds a resolved call to the graph as a step, joined on <paramref name="sourceOrderDeps"/>
    /// plus every step whose output its arguments read.</summary>
    private ParsedStep BuildStep(StepCall call, string? groupId, List<int> sourceOrderDeps)
    {
        var (invocation, member, phase, method, resultType, lowering, loop, _, _) = call;

        var dslNamespace = method.ContainingType?.ContainingNamespace;
        if (dslNamespace is { IsGlobalNamespace: false })
        {
            _dslNamespaces.Add(dslNamespace);
        }

        var index = _nextIndex++;
        var operation = call.Operation;

        var deps = new SortedSet<int>(sourceOrderDeps);
        deps.Add(_setupIndex);

        foreach (var read in lowering.Reads)
        {
            deps.UnionWith(_vars[read.Local].Producers);
        }

        var written = invocation.ArgumentList.Arguments;
        var lowered = lowering.Arguments.Select(a => a.Node).ToList();
        var invokeCall = BuildCall(
            invocation,
            member.WithExpression(call.Receiver),
            lowering.TypeArguments?.Node,
            lowered);
        var resourceClaims = BuildResourceClaims(written, method, resultType is not null, lowered);
        AddUses(method.GetAttributes());

        var (template, formatExpr) = DisplayNameBuilder.Build(_model, method, written, lowered);

        var step = new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, StepKey(call.KeyPrefix + operation, invocation, loop)),
            Phase = phase,
            OperationName = operation,
            HasResult = resultType is not null,
            ResultType = resultType is null
                ? PredefinedType(Token(SyntaxKind.ObjectKeyword))
                : TypeSyntaxFactory.From(resultType),
            InvokeCall = invokeCall,
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
    /// gets, a pattern variable its case node's result (or one element of it).
    /// </summary>
    private ExpressionSyntax Spell(VarSource source) => source switch
    {
        StepOutput step => InputsGet(_steps.First(s => s.Index == step.Index).ResultType, step.Index),
        GroupOutput group => ArrayCreationExpression(
                ArrayType(group.ElementType).WithRankSpecifiers(SingletonList(
                    ArrayRankSpecifier(SingletonSeparatedList<ExpressionSyntax>(
                        OmittedArraySizeExpression())))))
            .WithInitializer(InitializerExpression(
                SyntaxKind.ArrayInitializerExpression,
                SeparatedList<ExpressionSyntax>(group.Indices.Select(i => InputsGet(group.ElementType, i))))),
        CaseVariable variable => variable.Count == 1
            ? InputsGet(variable.Type, variable.Index)
            : MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                InputsGet(variable.Type, variable.Index),
                IdentifierName("Item" + (variable.Element + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
        _ => throw new System.InvalidOperationException("a failed output is never spelled"),
    };

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
    /// The step invocation as the generated file will call it: the method on the scenario's instance of
    /// its step class, with its type and value arguments lowered. Outer trivia is dropped — the call
    /// is re-hosted inside a lambda, and the emitter formats the whole file.
    /// </summary>
    private static InvocationExpressionSyntax BuildCall(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member,
        TypeArgumentListSyntax? typeArguments,
        List<ArgumentSyntax> arguments)
    {
        var emitted = arguments;

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
            => CallArguments.IndexOf(written, parameterName, position) is var i and >= 0
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
