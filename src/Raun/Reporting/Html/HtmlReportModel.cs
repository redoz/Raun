namespace Raun.Reporting.Html;

/// <summary>The full, self-contained report payload embedded into the HTML (design §4). All times are
/// pre-reduced to millisecond offsets from each scenario's start so the renderer does no clock math.</summary>
internal sealed record HtmlReportModel
{
    public required string GeneratedAtUtc { get; init; }
    public required ReportSummary Summary { get; init; }
    public required IReadOnlyList<ReportScenario> Scenarios { get; init; }
}

internal sealed record ReportSummary
{
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required double TotalMs { get; init; }
}

internal sealed record ReportScenario
{
    public required string ScenarioId { get; init; }
    public required string DisplayName { get; init; }
    public string? ClassDisplayName { get; init; }
    public required string MethodName { get; init; }
    public required string StartedAtUtc { get; init; }
    public required double DurationMs { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<ReportStep> Steps { get; init; }
    public required IReadOnlyList<ReportResource> Resources { get; init; }
    public required IReadOnlyList<ReportReference> References { get; init; }

    /// <summary>The scenario's contended-resource uses as <c>Type:Mode</c>; empty when none declared.</summary>
    public IReadOnlyList<string> Uses { get; init; } = [];

    /// <summary>Milliseconds admission held the scenario back behind a contended resource; 0 when none.</summary>
    public double WaitedMs { get; init; }

    /// <summary>Name of the resource that first refused the scenario; null when it never waited.</summary>
    public string? WaitedFor { get; init; }
}

internal sealed record ReportStep
{
    public required string StepId { get; init; }
    public required int Index { get; init; }
    public required string Label { get; init; }
    public required string Phase { get; init; }
    public required string DisplayName { get; init; }
    public required string Status { get; init; }
    public required double OffsetMs { get; init; }
    public required double DurationMs { get; init; }
    public required int Lane { get; init; }
    public required IReadOnlyList<int> DependsOn { get; init; }
    public string? GroupId { get; init; }
    public required IReadOnlyList<string> Logs { get; init; }
    public required IReadOnlyList<ReportEffect> Effects { get; init; }
    public string? Exception { get; init; }
    public string? SkipReason { get; init; }

    /// <summary>W3C trace id of the step's span when a trace listener was subscribed; null otherwise.</summary>
    public string? TraceId { get; init; }

    /// <summary>Span id of the step's span when one was recorded; null otherwise.</summary>
    public string? SpanId { get; init; }

    /// <summary>Text attachments the step recorded via <c>ctx.AddAttachment</c>, by name.</summary>
    public IReadOnlyList<ReportAttachment> Attachments { get; init; } = [];
}

internal sealed record ReportAttachment
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

internal sealed record ReportEffect
{
    public required string Verb { get; init; }
    public required string Type { get; init; }
    public required string Key { get; init; }
    public required double OffsetMs { get; init; }
    public string? Data { get; init; }
}

internal sealed record ReportResource
{
    public required string Type { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<ReportResourceEvent> Events { get; init; }
}

internal sealed record ReportResourceEvent
{
    public required string Verb { get; init; }
    public required double OffsetMs { get; init; }
    public required string StepId { get; init; }
}

/// <summary>One resource→resource lineage edge in the report, mapped from a step's recorded
/// producer References/Consumes targets. Endpoints are (Type, Key) pairs matching <see cref="ReportResource"/>.</summary>
internal sealed record ReportReference
{
    public required string SubjectType { get; init; }
    public required string SubjectKey { get; init; }
    public required string TargetType { get; init; }
    public required string TargetKey { get; init; }
    public required string Kind { get; init; } // "Reference" | "Consume" (from the verb)
}
