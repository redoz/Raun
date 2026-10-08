using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Raun.Generator.Diagnostics;

/// <summary>
/// Every Raun diagnostic. The parser reports the rules about lowering a scenario body (RAUN001–007,
/// RAUN011, RAUN013, RAUN017, RAUN019–021) through the generator; the analyzer reports the rest.
/// RAUN018 is not here: it is an MSBuild error Raun.props raises for an SDK below the floor.
/// </summary>
internal static class Descriptors
{
    private const string Category = "Raun.Usage";

    public static readonly DiagnosticDescriptor UnhandledException = new(
        "RAUN000",
        "Unhandled exception in Raun generator",
        "Raun failed to process a scenario: {0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MustBeAsyncTask = new(
        "RAUN001",
        "Scenario method must be async Task or async ValueTask",
        "Scenario method '{0}' must be declared 'async Task' or 'async ValueTask'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedStatement = new(
        "RAUN002",
        "Unsupported scenario statement",
        "Scenario statements must be an awaited step call (a method of a Given/When/Then step class, or of any Phase<TWorld>), an awaited tuple, or an awaited array of such calls",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedControlFlow = new(
        "RAUN003",
        "Unsupported control flow in scenario",
        "Loops and other control flow are not supported in scenario bodies — put the loop, retry, or polling inside a step. Only if/else (on an awaited step condition) shapes the graph.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotADslCall = new(
        "RAUN004",
        "Scenario step must be a step call",
        "Scenario steps must call a step: a public instance method of a step class deriving from Given<TWorld>, When<TWorld>, Then<TWorld>, or another Phase<TWorld>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidReturnType = new(
        "RAUN005",
        "DSL method has an unsupported return type",
        "DSL method '{0}' must return Task, Task<T>, ValueTask, or ValueTask<T>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidGroupElement = new(
        "RAUN006",
        "Parallel group element must be a step call",
        "Every element of a tuple/array parallel group must be a step call (a method of a Given/When/Then step class, or of any Phase<TWorld>)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidArgument = new(
        "RAUN007",
        "Scenario step argument is not lowerable",
        "Step argument cannot use '{0}': {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnboundPlaceholder = new(
        "RAUN008",
        "Display-name placeholder does not bind",
        "Display-name placeholder '{0}' of '{1}' does not bind: {2}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingResourceRole = new(
        "RAUN009",
        "Resource access must be declared",
        "Resource-typed {0} '{1}' must declare its access: [Read], [Edited], or [Deleted] on a parameter, or [Created], [Loaded], or [Edited] on the return — or be named in a producer's References/Consumes — there is no default",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidLineageSubject = new(
        "RAUN010",
        "Lineage target must name a step input",
        "'{0}' is not a valid lineage target for step '{1}' — References/Consumes must name a parameter (via nameof) or the step's own return (Subject.Return)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidCondition = new(
        "RAUN011",
        "Scenario condition must be an awaited step call",
        "A scenario branch must be 'if (await <step>)', 'if (await <step> is <pattern>)', or 'switch (await <step>)'; the condition here is not — a step's result must be what the branch decides on, and only an 'is' pattern may test it",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingParallelAccess = new(
        "RAUN013",
        "Parallel steps conflict on one resource",
        "Steps '{0}' and '{1}' run in parallel and both access '{2}', at least one with a mutating role ({3}); give one step a dependency on the other, or declare the access as [Read]",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StepContextInCleanup = new(
        "RAUN014",
        "Cleanup uses the registering step's context",
        "'{0}' is the context of the step that registered this cleanup; that step has already been reported by the time the cleanup runs, so anything logged or attached through it is lost — take the teardown context as the lambda parameter (OnTeardown(teardown => ...)) and use that instead",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ContendedResourceKind = new(
        "RAUN015",
        "Contended resource must declare exactly one kind",
        "'{0}' implements IContendedResource and must carry exactly one of [ExclusiveResource], [SharedResource], [PooledResource] with a capacity of at least 1",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InertContendedResourceUse = new(
        "RAUN016",
        "Contended resource use never constrains anything",
        "'{0}' is a shared contended resource that no scenario uses exclusively, so every [Uses<{0}>] in this assembly has no effect: shared uses never block each other; make one use LockMode.Exclusive, or remove the declarations",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: null,
        helpLinkUri: null,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>The scenario was not generated, for a reason no more specific rule names — a body that
    /// is not a block, a single step's result deconstructed, an array group merged across arms. It
    /// always says which. Every refusal the parser makes carries a diagnostic, so a scenario is either
    /// generated or reported; it cannot vanish from the test list.</summary>
    public static readonly DiagnosticDescriptor ScenarioNotGenerated = new(
        "RAUN017",
        "Scenario was not generated",
        "Scenario '{0}' was not generated. {1}.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnfollowableStepReceiver = new(
        "RAUN019",
        "Step receiver cannot be followed",
        "Raun cannot follow '{0}' to the step class this call runs on: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScenarioClassShape = new(
        "RAUN020",
        "Scenario does not fit its class",
        "Scenario '{0}' {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor WorldMismatch = new(
        "RAUN021",
        "Step class belongs to another world",
        "'{0}' is a step class of '{1}', but this scenario's world is '{2}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>One step-class instance serves every step of a scenario, parallel siblings included,
    /// so a writable field or property on it is state those steps race on. A warning, not an error: a
    /// field guarded by a lock or Interlocked is fine, and only its author knows.</summary>
    public static readonly DiagnosticDescriptor MutableStepClassState = new(
        "RAUN022",
        "Step class holds mutable state",
        "'{0}' is mutable state on step class '{1}', shared by {2}, parallel steps included; make it readonly, or keep the state in the world or in a step result",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>Every descriptor by id — how a diagnostic the parser carries as plain values (an id and
    /// its arguments) becomes a reportable one. Declared last: static fields initialize in order.</summary>
    public static readonly IReadOnlyDictionary<string, DiagnosticDescriptor> ById = new[]
    {
        UnhandledException, MustBeAsyncTask, UnsupportedStatement, UnsupportedControlFlow, NotADslCall,
        InvalidReturnType, InvalidGroupElement, InvalidArgument, UnboundPlaceholder, MissingResourceRole,
        UnfollowableStepReceiver, ScenarioClassShape, WorldMismatch, MutableStepClassState,
        InvalidLineageSubject, InvalidCondition, ConflictingParallelAccess,
        StepContextInCleanup, ContendedResourceKind, InertContendedResourceUse, ScenarioNotGenerated,
    }.ToDictionary(d => d.Id);
}
