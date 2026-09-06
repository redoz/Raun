using System.Diagnostics;
using System.Reflection;

namespace Raun;

/// <summary>
/// Raun's <see cref="ActivitySource"/> and the attribute and event names it emits. Raun <b>emits</b>
/// traces and never exports them: subscribe with OpenTelemetry
/// (<c>.AddSource(RaunTelemetry.SourceName)</c>) or an <see cref="ActivityListener"/>. With no
/// listener every <c>StartActivity</c> returns null and nothing is recorded.
/// </summary>
/// <remarks>
/// Span tree: one root span per <b>scenario</b> (the unit you open in a trace viewer), with a child
/// span per step and per teardown. The run has its own small root span, and every scenario span
/// <em>links</em> to it rather than nesting under it, so a whole suite never becomes one giant trace
/// and head sampling can decide per scenario. Because a step span is <see cref="Activity.Current"/>
/// while the step body runs, every outgoing <c>HttpClient</c> call carries its <c>traceparent</c>, and
/// the system under test's own spans land under the step that provoked them.
/// </remarks>
#pragma warning disable CA1034 // Nested static classes namespace the constants: RaunTelemetry.Attributes.Step reads as intended.
public static class RaunTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name to subscribe to.</summary>
    public const string SourceName = "Raun";

    /// <summary>The source every Raun span comes from.</summary>
    public static ActivitySource Source { get; } = new(SourceName, Version());

    /// <summary>Attribute names. Test-related ones follow the OpenTelemetry semantic conventions.</summary>
    public static class Attributes
    {
        /// <summary>Scenario display name (semconv).</summary>
        public const string TestSuiteName = "test.suite.name";

        /// <summary>Step display name (semconv).</summary>
        public const string TestCaseName = "test.case.name";

        /// <summary>Step outcome: <c>pass</c>, <c>fail</c>, or <c>skipped</c> (semconv, extended).</summary>
        public const string TestCaseResultStatus = "test.case.result.status";

        /// <summary>Scenario outcome: <c>success</c>, <c>failure</c>, or <c>aborted</c> (semconv).</summary>
        public const string TestSuiteRunStatus = "test.suite.run.status";

        /// <summary>Id of the run a scenario belongs to, so a run can be queried across its traces.</summary>
        public const string Run = "raun.run";

        /// <summary>Stable scenario id.</summary>
        public const string Scenario = "raun.scenario";

        /// <summary>Stable step id.</summary>
        public const string Step = "raun.step";

        /// <summary>Phase marker name (Given/When/Then or a custom marker).</summary>
        public const string StepPhase = "raun.step.phase";

        /// <summary>DSL operation (method) name.</summary>
        public const string StepOperation = "raun.step.operation";

        /// <summary>The scenario's contended-resource uses, <c>Type:Mode</c> comma-separated; absent when none.</summary>
        public const string ScenarioUses = "raun.scenario.uses";

        /// <summary>Milliseconds admission held the scenario back behind a contended resource; absent when zero.</summary>
        public const string ScenarioWaitedMs = "raun.scenario.waited_ms";

        /// <summary>Name of the resource that first refused the scenario; set with <see cref="ScenarioWaitedMs"/>.</summary>
        public const string ScenarioWaitedFor = "raun.scenario.waited_for";

        /// <summary>Source file of the step's statement (semconv).</summary>
        public const string CodeFilePath = "code.file.path";

        /// <summary>Source line of the step's statement (semconv).</summary>
        public const string CodeLineNumber = "code.line.number";
    }

    /// <summary>Span event names.</summary>
    public static class Events
    {
        /// <summary>A step log line; tag <c>message</c>.</summary>
        public const string Log = "log";

        /// <summary>A resource effect; tags <c>verb</c>, <c>identity</c>, and <c>conflict</c> when the claim was refused.</summary>
        public const string Resource = "raun.resource";

        /// <summary>A step that did not run, recorded on the scenario span; tags <c>step</c>, <c>status</c>, <c>reason</c>.</summary>
        public const string StepSkipped = "raun.step.skipped";
    }

    private static string Version()
    {
        var informational = typeof(RaunTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
#pragma warning restore CA1034
