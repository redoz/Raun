namespace Raun.Scheduling;

/// <summary>
/// The clock scenario and step deadlines are measured on, for a test that moves time itself.
/// </summary>
/// <param name="timestamp">Now, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
/// <param name="watchdogLooked">Called each time the watchdog has finished judging every deadline
/// against one reading of the clock.</param>
internal sealed class DeadlineClock(Func<long> timestamp, Action? watchdogLooked = null)
{
    internal Func<long> Timestamp { get; } = timestamp;

    internal Action? WatchdogLooked { get; } = watchdogLooked;
}
