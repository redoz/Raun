using System.Globalization;

namespace Raun.Model;

/// <summary>
/// One line of a step's log and when it was written, as an offset from the start of the scenario —
/// a timer, not a timestamp, because "2.3 seconds in" is what you want to know when reading a run.
/// Under the scheduler's simulated-time mode the offset comes from the step's simulated clock, so
/// the sample report stays deterministic.
/// </summary>
public readonly record struct LogEntry
{
    /// <summary>Creates a log line from its elapsed time and legacy text.</summary>
    public LogEntry(TimeSpan Elapsed, string Message)
    {
        this.Elapsed = Elapsed;
        Content = StepMessage.FromText(Message);
    }

    /// <summary>Time since the scenario started when the line was written.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>The plain-text projection of <see cref="Content"/>.</summary>
    public string Message
    {
        get => Content?.Text!;
        init => Content = StepMessage.FromText(value);
    }

    /// <summary>The frozen message, or null for a default-initialized entry.</summary>
    public StepMessage? Content { get; init; }

    /// <summary>Deconstructs using the legacy elapsed time and text shape.</summary>
    public void Deconstruct(out TimeSpan Elapsed, out string Message)
    {
        Elapsed = this.Elapsed;
        Message = this.Message;
    }

    /// <summary>Compares elapsed time and the ordered immutable message parts by value.</summary>
    public bool Equals(LogEntry other)
        => Elapsed == other.Elapsed
            && (Content is null || other.Content is null
                ? Content is null && other.Content is null
                : Content.Parts.SequenceEqual(other.Content.Parts));

    /// <summary>Hashes elapsed time and the ordered message parts.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Elapsed);
        if (Content is not null)
        {
            foreach (var part in Content.Parts) { hash.Add(part); }
        }
        return hash.ToHashCode();
    }

    /// <summary>Renders as <c>+1.234s message</c>.</summary>
    public override string ToString()
        => "+" + Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s " + Message;
}
