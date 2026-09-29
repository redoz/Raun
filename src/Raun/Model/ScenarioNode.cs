namespace Raun.Model;

/// <summary>
/// One node in a scenario's execution graph: the metadata the runner reports plus the delegate
/// that runs the underlying DSL operation. Produced by the generator; consumed by the scheduler.
/// </summary>
public sealed class ScenarioNode
{
    /// <summary>Position of this node in the scenario's node list (its identity within the graph).</summary>
    public required int Index { get; init; }

    /// <summary>Stable, runner-independent id for this step.</summary>
    public required string StepId { get; init; }

    /// <summary>Phase marker name: <c>Given</c>, <c>When</c>, or <c>Then</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>The DSL operation (method) name this node invokes.</summary>
    public required string OperationName { get; init; }

    /// <summary>Display-name template, with any non-constant placeholders left for runtime formatting.</summary>
    public required string DisplayNameTemplate { get; init; }

    /// <summary>Source file of the originating scenario statement, if known.</summary>
    public string? SourceFile { get; init; }

    /// <summary>1-based source line of the originating scenario statement, or 0 if unknown.</summary>
    public int SourceLine { get; init; }

    /// <summary>Per-step timeout, or null to inherit the scenario timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Indices of the nodes that must complete successfully before this node runs.</summary>
    public required IReadOnlyList<int> DependsOn { get; init; }

    /// <summary>Label shared by sibling nodes of a tuple/array parallel group, for reporting.</summary>
    public string? GroupId { get; init; }

    /// <summary>Branch conditions gating this node; ALL must hold for it to run. Empty for an
    /// unconditional node.</summary>
    public IReadOnlyList<Guard> Guards { get; init; } = [];

    /// <summary>
    /// For a synthetic merge (phi) node, the mutually-exclusive candidate producers whose outputs
    /// this node selects between — the ONLY any-of semantics in the graph. Exactly one may pass; its
    /// output becomes this node's output. A single source is a pass-through alias (the missing arm of
    /// a bare <c>if</c>). Empty for an ordinary node.
    /// </summary>
    public IReadOnlyList<int> MergeSources { get; init; } = [];

    /// <summary>
    /// Ordering-only predecessors: nodes that must be terminal before this node runs, without any
    /// value flowing. Unlike <see cref="DependsOn"/>, a <see cref="StepStatus.NotTaken"/> entry does
    /// not make this node not-taken — the branch simply was not chosen and this node runs anyway;
    /// Failed and Skipped still cascade to Skipped. The generator uses it for the statement after an
    /// <c>if</c>: it depends on the condition (or the merges) for values, and waits for every arm's
    /// last steps so nothing after the <c>if</c> runs concurrently with what is inside it.
    /// </summary>
    public IReadOnlyList<int> WaitsFor { get; init; } = [];

    /// <summary>True for generator plumbing (merge/pass-through nodes) that is not a business step:
    /// excluded from MTP discovery and step numbering, retained in the HTML report model.</summary>
    public bool IsSynthetic { get; init; }

    /// <summary>
    /// True for the scenario's single generator-emitted teardown node. The INVERSE of
    /// <see cref="IsSynthetic"/>: it is discovered and numbered like an ordinary step (users must see
    /// a failing cleanup in CI), and only the scheduler and the report treat it specially.
    /// </summary>
    public bool IsTeardown { get; init; }

    /// <summary>
    /// True for the Setup node of a scenario with a world: the first node, which creates the world and
    /// on which every step depends, so a failed Setup skips them all. Discovered and reported like an
    /// ordinary step, but unnumbered, so adding a world does not renumber a scenario's steps.
    /// </summary>
    public bool IsSetup { get; init; }

    /// <summary>
    /// For a condition node, coerces this step's (boxed) output to the branch value. The generator
    /// emits <c>static o =&gt; ((T)o!) ? true : false</c> so Roslyn selects <c>bool</c>, an implicit
    /// conversion, or <c>operator true</c> at compile time — the scheduler never reflects. Null for a
    /// node that gates nothing.
    /// </summary>
    public Func<object?, bool>? EvaluateCondition { get; init; }

    /// <summary>
    /// Runs the underlying DSL operation. Reads its arguments from <see cref="IStepInputs"/>,
    /// returns the (boxed) output, or null for a non-result <c>Task</c>/<c>ValueTask</c>.
    /// </summary>
    public required Func<IStepInputs, ScenarioContext, Task<object?>> Invoke { get; init; }

    /// <summary>
    /// Optional runtime formatter for the display name when the template binds to runtime values
    /// (a prior step's output). When null, <see cref="DisplayNameTemplate"/> is used verbatim.
    /// </summary>
    public Func<IStepInputs, string>? FormatDisplayName { get; init; }
}
