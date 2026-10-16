using Raun;
using Xunit;

namespace AppointmentTests;

[PooledResource(2)]
public sealed class ScannerStations : IContendedResource;

[ExclusiveResource]
public sealed class CalibrationConsole : IContendedResource;

public sealed class CapacityScenarios : Raun.Scenarios
{
    public ScannerWhen When => Steps<ScannerWhen>();

    // Three scenarios share two stations; admission waits when both are occupied.
    [Scenario("a morning appointment uses a scanner station")]
    [Uses<ScannerStations>]
    public async Task BookMorning()
    {
        await When.ScanAppointment("Morning");
    }

    [Scenario("an afternoon appointment uses a scanner station")]
    [Uses<ScannerStations>]
    public async Task BookAfternoon()
    {
        await When.ScanAppointment("Afternoon");
    }

    [Scenario("an evening appointment uses a scanner station")]
    [Uses<ScannerStations>]
    public async Task BookEvening()
    {
        await When.ScanAppointment("Evening");
    }

    // Maintenance takes every station and the single calibration console until teardown ends.
    [Scenario("the clinic recalibrates every scanner station")]
    [Uses<ScannerStations>(LockMode.Exclusive)]
    [Uses<CalibrationConsole>]
    public async Task Recalibrate()
    {
        await When.RecalibrateStations();
    }
}

public sealed class ScannerWhen : When
{
    [StepName("When scanning the {appointment} appointment")]
    public async Task ScanAppointment(string appointment)
    {
        await ScannerFleet.ScanAsync();
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(250));
        Context.Log($"Scanned the {appointment} appointment.");
    }

    [StepName("When recalibrating all scanner stations")]
    public async Task RecalibrateStations()
    {
        await ScannerFleet.RecalibrateAsync();
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(500));
        Context.OnTeardown(teardown =>
        {
            teardown.Log("Returned every station to service.");
            return Task.CompletedTask;
        });
    }
}

// A stand-in for the clinic's shared hardware; checks catch overlapping maintenance or overbooking.
internal static class ScannerFleet
{
    private static int activeScans;
    private static int calibrating;

    public static async Task ScanAsync()
    {
        var active = Interlocked.Increment(ref activeScans);
        try
        {
            Assert.InRange(active, 1, 2);
            Assert.Equal(0, Volatile.Read(ref calibrating));
            await Task.Yield();
        }
        finally
        {
            Interlocked.Decrement(ref activeScans);
        }
    }

    public static async Task RecalibrateAsync()
    {
        Assert.Equal(0, Volatile.Read(ref activeScans));
        Assert.Equal(0, Interlocked.Exchange(ref calibrating, 1));
        try
        {
            await Task.Yield();
        }
        finally
        {
            Interlocked.Exchange(ref calibrating, 0);
        }
    }
}
