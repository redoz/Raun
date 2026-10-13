namespace Raun.Model;

/// <summary>The outcome of running (or skipping) a single scenario step.</summary>
public sealed class StepResult
{
    private StepMessage? _titleMessage;
    private IReadOnlyList<StepMessage> _noteMessages = [];
    private IReadOnlyList<StepMessage> _legacyLogMessages = [];
    private bool _hasTypedTitle;
    private bool _hasTypedNotes;

    /// <summary>The node this result describes.</summary>
    public required ScenarioNode Node { get; init; }

    /// <summary>The formatted display name (runtime placeholders resolved).</summary>
    public required string DisplayName
    {
        get => TitleMessage.Text;
        init
        {
            if (!_hasTypedTitle) { _titleMessage = StepMessage.FromText(value); }
        }
    }

    /// <summary>The frozen title: input parts captured before the step started, with return parts resolved after successful completion.</summary>
    public StepMessage TitleMessage
    {
        get => _titleMessage ?? StepMessage.FromText(Node.DisplayNameTemplate);
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _titleMessage = value;
            _hasTypedTitle = true;
        }
    }

    /// <summary>Final status of the step.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Absolute wall-clock instant the step began (stamped scheduler-side via an injected
    /// <see cref="TimeProvider"/>). Skipped steps carry the instant at skip time; <see cref="Duration"/>
    /// stays zero. <c>FinishedAt</c> is derived (<c>StartedAt + Duration</c>), not stored.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wall-clock duration the operation ran (zero for skipped steps).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The thrown exception when <see cref="Status"/> is <see cref="StepStatus.Failed"/>.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Why the step was skipped, e.g. <c>"dependency failed: creating an appointment"</c>.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Explanations attached to this step, separate from its diagnostic logs.</summary>
    public IReadOnlyList<string> Notes
    {
        get => _noteMessages.Select(message => message.Text).ToArray();
        init
        {
            if (!_hasTypedNotes) { _noteMessages = value.Select(StepMessage.FromText).ToArray(); }
        }
    }

    /// <summary>Frozen explanations attached to this step.</summary>
    public IReadOnlyList<StepMessage> NoteMessages
    {
        get => _noteMessages;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _noteMessages = value;
            _hasTypedNotes = true;
        }
    }

    /// <summary>Log lines collected during the step, messages only. Resource events the step recorded
    /// appear here too, in order, as <c>[resource] {Verb} {Identity}</c> lines.</summary>
    public IReadOnlyList<string> Logs
    {
        get => LogEntries.Count > 0
            ? LogEntries.Select(entry => entry.Message).ToArray()
            : _legacyLogMessages.Select(message => message.Text).ToArray();
        init => _legacyLogMessages = value.Select(StepMessage.FromText).ToArray();
    }

    /// <summary>The same lines with their offset from the scenario's start. Populated by the scheduler;
    /// a result built elsewhere may leave it empty, in which case sinks fall back to <see cref="Logs"/>.</summary>
    public IReadOnlyList<LogEntry> LogEntries { get; init; } = [];

    /// <summary>Attachments collected during the step.</summary>
    public IReadOnlyDictionary<string, string> Attachments { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Resource effects (lifecycle acquires) the step recorded, in call order.</summary>
    public IReadOnlyList<ResourceEffect> Effects { get; init; } = [];

    /// <summary>Lineage relations the step recorded from a producer's [Created]/[Loaded]/[Edited] References/Consumes targets.</summary>
    public IReadOnlyList<ResourceLineageRelation> Lineage { get; init; } = [];

    /// <summary>W3C trace id of the step's span, when a listener was subscribed to
    /// <see cref="RaunTelemetry.Source"/>; otherwise null. Paste it into your trace viewer.</summary>
    public string? TraceId { get; init; }

    /// <summary>Span id of the step's span, when one was recorded; otherwise null.</summary>
    public string? SpanId { get; init; }
}
