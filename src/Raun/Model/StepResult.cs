namespace Raun.Model;

/// <summary>The outcome of running (or skipping) a single scenario step.</summary>
public sealed class StepResult
{
    private StepMessage? _titleMessage;

    /// <summary>The node this result describes.</summary>
    public required ScenarioNode Node { get; init; }

    /// <summary>The title as plain text: <see cref="TitleMessage"/> without its structure.</summary>
    public string DisplayName => TitleMessage.Text;

    /// <summary>The frozen title: input parts captured before the step started, with return parts
    /// resolved after successful completion. The node's display-name template when not set.</summary>
    public StepMessage TitleMessage
    {
        get => _titleMessage ?? StepMessage.FromText(Node.DisplayNameTemplate);
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _titleMessage = value;
        }
    }
    /// <summary>Final status of the step.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Absolute wall-clock instant the step began (stamped scheduler-side via an injected
    /// <see cref="TimeProvider"/>), or null when the step never reached the scheduler because its
    /// scenario was skipped after a failed preflight. A step the scheduler skipped carries the instant
    /// at skip time; <see cref="Duration"/> stays zero. <c>FinishedAt</c> is derived
    /// (<c>StartedAt + Duration</c>), not stored.</summary>
    public required DateTimeOffset? StartedAt { get; init; }

    /// <summary>Wall-clock duration the operation ran (zero for skipped steps).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The thrown exception when <see cref="Status"/> is <see cref="StepStatus.Failed"/>.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Why the step was skipped, e.g. <c>"dependency failed: creating an appointment"</c>.</summary>
    public string? SkipReason { get; init; }

    /// <summary>The notes as plain text: <see cref="NoteMessages"/> without their structure.</summary>
    public IReadOnlyList<string> Notes => NoteMessages.Select(message => message.Text).ToArray();

    /// <summary>Frozen explanations attached to this step, separate from its diagnostic logs.</summary>
    public IReadOnlyList<StepMessage> NoteMessages { get; init; } = [];

    /// <summary>The log lines as plain text: the messages of <see cref="LogEntries"/>.</summary>
    public IReadOnlyList<string> Logs => LogEntries.Select(entry => entry.Message).ToArray();

    /// <summary>Log lines collected during the step, each with its offset from the scenario's start.
    /// Resource events the step recorded appear here too, in order, as
    /// <c>[resource] {Effect} {Identity}</c> lines.</summary>
    public IReadOnlyList<LogEntry> LogEntries { get; init; } = [];
    /// <summary>Attachments collected during the step.</summary>
    public IReadOnlyDictionary<string, string> Attachments { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Resource effects (lifecycle acquires) the step recorded, in call order.</summary>
    public IReadOnlyList<ResourceObservation> Effects { get; init; } = [];

    /// <summary>The independent role-named resource links recorded by this step.</summary>
    public IReadOnlyList<ResourceLink> Links { get; init; } = [];

    /// <summary>W3C trace id of the step's span, when a listener was subscribed to
    /// <see cref="RaunTelemetry.Source"/>; otherwise null. Paste it into your trace viewer.</summary>
    public string? TraceId { get; init; }

    /// <summary>Span id of the step's span, when one was recorded; otherwise null.</summary>
    public string? SpanId { get; init; }
}
