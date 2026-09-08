using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>The original-source span of a step's DSL call, for span-form #line emission. 0-based
/// (Roslyn LinePosition); the emitter converts to the directive's 1-based form.</summary>
internal readonly record struct SourceSpan(
    string File, int StartLine, int StartChar, int EndLine, int EndChar);

/// <summary>
/// A single resource role lowered from a <c>[Created]/[Loaded]/[Read]/[Edited]/[Deleted]</c> attribute
/// on a step's parameter or return value, or a <c>Reference</c>/<c>Consume</c> claim synthesized from a
/// producer's <c>References</c>/<c>Consumes</c> lineage. <see cref="Verb"/> is the runtime
/// <c>ResourceContext</c> method name (Read/Load/Create/Edit/Delete/Reference/Consume);
/// <see cref="Expression"/> is the rewritten argument expression (in terms of <c>__inputs</c>) for a
/// parameter role, the lineage target's expression for a synthesized claim, or <c>__r</c> for a return role.
/// </summary>
internal readonly record struct ResourceRoleClaim(string Verb, ExpressionSyntax Expression, bool IsReturn)
{
    /// <summary>For a synthesized Reference/Consume claim, the producing subject's instance expression
    /// (a parameter's rewritten argument, or <c>__r</c>) — emitted as the trailing argument so the
    /// runtime records subject→target. Empty for plain role claims.</summary>
    public IReadOnlyList<ExpressionSyntax> SubjectExpressions { get; init; } = [];
}

/// <summary>A lowered branch guard: the node runs only when node <see cref="ConditionIndex"/> passed
/// and its condition evaluates to <see cref="WhenValue"/>. Mirrors <c>Raun.Model.Guard</c>.</summary>
internal readonly record struct ParsedGuard(int ConditionIndex, bool WhenValue);

/// <summary>One reduced contended-resource use of a scenario: the token type (fully qualified, for
/// <c>typeof</c>) and the mode name (<c>Shared</c> or <c>Exclusive</c>) as spelled on
/// <c>Raun.LockMode</c>. <see cref="SortKey"/> is the type's fully-qualified display string — an
/// ordering key so emission is deterministic, never emitted itself.</summary>
internal readonly record struct ParsedUse(TypeSyntax Resource, string Mode, string SortKey);

/// <summary>Why a scenario was not lowered: the innermost statement the parser rejected (or the
/// method name, for a body-less method). Carries the span as plain values — never a
/// <c>Location</c>, which would pin a syntax tree in the incremental pipeline — and is rebuilt into
/// an external-file location when reported.</summary>
internal readonly record struct ParseRejection(
    string ScenarioName, string File, int SpanStart, int SpanLength, SourceSpan Lines);

/// <summary>The parser's verdict on one scenario: exactly one of the two is set.</summary>
internal readonly record struct ParseOutcome(ParsedScenario? Scenario, ParseRejection? Rejection);

/// <summary>A lowered scenario ready for emission.</summary>
internal sealed record ParsedScenario
{
    public string MethodFullName { get; init; } = "";
    public string Namespace { get; init; } = "";       // declaring namespace, "" for global
    public string TypeName { get; init; } = "";        // declaring type, nested joined with '+'
    public string SafeName { get; init; } = "";        // identifier-safe form for generated members
    public string ScenarioId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? ClassDisplayName { get; init; }
    public string? SourceFile { get; init; }
    public int SourceLine { get; init; }
    public int TimeoutMs { get; init; }
    public IReadOnlyList<UsingDirectiveSyntax> Usings { get; init; } = [];
    public IReadOnlyList<ParsedStep> Steps { get; init; } = [];

    /// <summary>The scenario teardown policy as the underlying <c>Raun.Run</c> value.</summary>
    public int TeardownPolicy { get; init; }

    /// <summary>Every [Uses&lt;T&gt;] the scenario is subject to, one per type (Exclusive wins),
    /// sorted by <see cref="ParsedUse.SortKey"/> so emission is deterministic. Empty ⇒ no initializer.</summary>
    public IReadOnlyList<ParsedUse> Uses { get; init; } = [];
}

/// <summary>One lowered step (graph node).</summary>
internal sealed record ParsedStep
{
    public int Index { get; init; }
    public string StepId { get; init; } = "";
    public string Phase { get; init; } = "";           // Given/When/Then
    public string OperationName { get; init; } = "";
    public string? SourceFile { get; init; }
    public int SourceLine { get; init; }

    /// <summary>Original span of the DSL invocation, for column-accurate #line mapping; null when
    /// the input was parsed without a path (e.g. pathless snapshot harness) ⇒ no directive.</summary>
    public SourceSpan? CallSpan { get; init; }

    public int TimeoutMs { get; init; }
    public IReadOnlyList<int> DependsOn { get; init; } = [];
    public string? GroupId { get; init; }

    /// <summary>True when the DSL method returns a value (Task&lt;T&gt;/ValueTask&lt;T&gt;).</summary>
    public bool HasResult { get; init; }

    /// <summary>Fully-qualified output type (the T), or <c>object</c> when there is no result.</summary>
    public TypeSyntax ResultType { get; init; } = PredefinedType(Token(SyntaxKind.ObjectKeyword));

    /// <summary>The rewritten DSL invocation, e.g. <c>Given.PatientExists("Jane")</c>. Null for a
    /// synthetic (merge/pass-through) or teardown node, which never invokes anything.</summary>
    public InvocationExpressionSyntax? InvokeCall { get; init; }

    /// <summary>Display name with constant placeholders already substituted.</summary>
    public string DisplayNameTemplate { get; init; } = "";

    /// <summary>
    /// When non-null, a string expression (in terms of <c>__inputs</c>) for the runtime display-name
    /// formatter; null when the display name is fully constant.
    /// </summary>
    public ExpressionSyntax? FormatExpression { get; init; }

    /// <summary>
    /// Resource roles lowered from the step's role attributes, in declaration order (parameter roles
    /// first, then a return role). Empty when the step declares no roles ⇒ the emitter inserts nothing.
    /// </summary>
    public IReadOnlyList<ResourceRoleClaim> ResourceClaims { get; init; } = [];

    /// <summary>Branch guards gating this step; all must hold. Empty for an unconditional step.</summary>
    public IReadOnlyList<ParsedGuard> Guards { get; init; } = [];

    /// <summary>Mutually exclusive candidate producers for a merge (phi) node, or the single source of
    /// a pass-through alias. Empty for an ordinary step.</summary>
    public IReadOnlyList<int> MergeSources { get; init; } = [];

    /// <summary>Ordering-only predecessors (see <c>Raun.Model.ScenarioNode.WaitsFor</c>): the last
    /// steps of every arm of the <c>if</c> this statement follows. Empty for most steps.</summary>
    public IReadOnlyList<int> WaitsFor { get; init; } = [];

    /// <summary>True for generator plumbing (merge/pass-through) rather than a business step.</summary>
    public bool IsSynthetic { get; init; }

    /// <summary>True for the scenario's single teardown node — discovered and numbered like an
    /// ordinary step, but run by the scheduler after the DAG rather than as part of it.</summary>
    public bool IsTeardown { get; init; }

    /// <summary>When this step is used as an <c>if</c> condition, its fully-qualified result type — the
    /// cast target in the emitted <c>EvaluateCondition</c> coercion. Null otherwise.</summary>
    public TypeSyntax? ConditionCoercionType { get; init; }
}
