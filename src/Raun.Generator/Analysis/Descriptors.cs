using Microsoft.CodeAnalysis;

namespace Raun.Generator.Analysis;

/// <summary>Diagnostics for the supported scenario subset (see the design's "Analyzer Rules").</summary>
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
        "Scenario statements must be an awaited phase-marker call (Given/When/Then, or any type implementing Raun.IPhase), an awaited tuple, or an awaited array of such calls",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedControlFlow = new(
        "RAUN003",
        "Unsupported control flow in scenario",
        "Loops and other control flow are not supported in scenario bodies — put the loop, retry, or polling inside a step. Only if/else (on an awaited phase-marker condition) shapes the graph.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotADslCall = new(
        "RAUN004",
        "Scenario step must be a phase-marker call",
        "Scenario steps must call a static extension member on a phase marker (Given/When/Then, or any type implementing Raun.IPhase)",
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
        "Parallel group element must be a phase-marker call",
        "Every element of a tuple/array parallel group must be a phase-marker call (Given/When/Then, or any type implementing Raun.IPhase)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidArgument = new(
        "RAUN007",
        "Scenario step argument is not lowerable",
        "Argument '{0}' must be a prior step output, a scenario parameter, or a constant",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnboundPlaceholder = new(
        "RAUN008",
        "Display-name placeholder does not bind to a parameter",
        "Display-name placeholder '{0}' does not match any parameter of '{1}'",
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
        "Scenario condition must be an awaited phase-marker call",
        "An 'if' condition in a scenario must be an awaited phase-marker call (Given/When/Then, or any type implementing Raun.IPhase) whose result is usable as a C# condition (bool, an implicit conversion to bool, or 'operator true')",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnmergeableLocal = new(
        "RAUN012",
        "Conditionally assigned local has no step-produced definition",
        "'{0}' is assigned inside a branch but has no step-produced definition outside it, so there is nothing to merge against — give it a prior step output, or assign it in every branch",
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
}
