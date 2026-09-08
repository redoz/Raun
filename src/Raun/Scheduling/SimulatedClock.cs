namespace Raun.Scheduling;

/// <summary>
/// A thread-safe <see cref="TimeProvider"/> whose only motion comes from explicit
/// <see cref="Advance(TimeSpan)"/> calls — it never tracks real wall-clock time. Seeded at a base
/// instant, it advances by exactly the requested deltas, so step bodies can simulate durations
/// (e.g. via <c>ScenarioContext.SimulateElapsed</c>) without any real waiting.
/// <para>
/// All of <see cref="GetUtcNow"/>, <see cref="GetTimestamp"/>, and <see cref="TimeProvider.GetElapsedTime(long)"/>
/// derive from a single <see cref="Interlocked"/>-guarded tick counter, so they stay mutually
/// consistent and concurrent advances from many threads sum exactly. Each scheduled step gets its
/// own <see cref="SimulatedClock"/> so parallel siblings cannot corrupt one another's measured
/// duration.
/// </para>
/// </summary>
internal sealed class SimulatedClock : TimeProvider
{
    private readonly long _baseTicks;
    private long _advancedTicks;

    /// <param name="baseInstant">The instant returned by <see cref="GetUtcNow"/> before any advance.</param>
    public SimulatedClock(DateTimeOffset baseInstant) => _baseTicks = baseInstant.UtcTicks;

    /// <summary>The total amount this clock has been advanced since it was seeded.</summary>
    public TimeSpan Advanced => TimeSpan.FromTicks(Interlocked.Read(ref _advancedTicks));

    /// <summary>Moves the clock forward by <paramref name="delta"/>. Thread-safe; concurrent calls sum exactly.</summary>
    public void Advance(TimeSpan delta) => Interlocked.Add(ref _advancedTicks, delta.Ticks);

    /// <summary>The seed instant plus everything that has been advanced so far.</summary>
    public override DateTimeOffset GetUtcNow() =>
        new(_baseTicks + Interlocked.Read(ref _advancedTicks), TimeSpan.Zero);

    /// <summary>
    /// Frequency is one tick (100 ns) per unit so timestamps map directly onto advanced ticks and
    /// <see cref="TimeProvider.GetElapsedTime(long)"/> reproduces advances exactly.
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>A monotonic timestamp derived from the same advanced-tick counter as <see cref="GetUtcNow"/>.</summary>
    public override long GetTimestamp() => _baseTicks + Interlocked.Read(ref _advancedTicks);
}
