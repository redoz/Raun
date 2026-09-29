using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using Raun;
using Xunit;

namespace AppointmentTests.StepClasses;

// The step-class authoring model (docs/superpowers/specs/2026-09-29-step-classes-and-scenario-world-design.md),
// next to the extension-member DSL the rest of this sample uses. Two kinds of state are kept apart:
//
//  - the WORLD is scenario infrastructure: a seeded TestIsolation, created once per scenario in its
//    Setup node, shared by every step, disposed in teardown whatever happened;
//  - customers, appointments and cancellations are STEP RESULTS, passed from step to step as
//    arguments, so the dependency graph stays visible in the scenario.
//
// Inside a step, `Context` is Raun's (cancellation, logs, teardown) and `World` is yours.

/// <summary>The seed a scenario's isolation is created with. Yours, not Raun's: the world reads it
/// from the scenario method.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IsolationSeedAttribute(int seed) : Attribute
{
    public int Seed { get; } = seed;
}

/// <summary>
/// A stand-in for a suite's own test isolation: a tenant derived from a seed, and the stub server the
/// scenario's dependencies are pointed at. Sibling steps configure stubs at the same time, so the
/// stub server is safe for concurrent use; the world only guarantees that its reference to it never
/// changes.
/// </summary>
public sealed class TestIsolation
{
    private TestIsolation(int seed) => Seed = seed;

    public int Seed { get; }

    public string Tenant => $"tenant-{Seed}";

    public StubServer Stubs { get; } = new();

    public static async Task<TestIsolation> CreateAsync(int seed, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return new TestIsolation(seed);
    }

    public Task ReleaseAsync()
    {
        Stubs.Reset();
        return Task.CompletedTask;
    }
}

/// <summary>Stubbed dependencies: what each service answers, and who called it.</summary>
public sealed class StubServer
{
    private readonly ConcurrentDictionary<string, bool> _accepts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _calls = new();

    public void Accept(string service) => _accepts[service] = true;

    public void Fail(string service) => _accepts[service] = false;

    /// <summary>Calls a stubbed service; false when it is stubbed to fail. An unstubbed service is a
    /// test bug, so it throws.</summary>
    public bool Call(string service, string payload)
    {
        _calls.Enqueue($"{service}: {payload}");
        return _accepts.TryGetValue(service, out var accepts)
            ? accepts
            : throw new InvalidOperationException($"'{service}' was never stubbed");
    }

    public IReadOnlyCollection<string> Calls => _calls;

    public void Reset()
    {
        _accepts.Clear();
        _calls.Clear();
    }
}

public sealed record Customer(string Name)
{
    public override string ToString() => Name;
}

public sealed record Booking(string Id, Customer Customer, DateOnly Day)
{
    public override string ToString() => Id;
}

public sealed record Cancellation(Booking Booking, bool CalendarUpdated)
{
    public override string ToString() => $"the cancellation of {Booking.Id}";
}

/// <summary>
/// The per-scenario world. Created by a static factory rather than a constructor plus an init method,
/// so a world that exists is a world that finished initializing — and whatever was made before a
/// failure is released by the cleanup registered the moment it existed.
/// </summary>
public sealed class ClinicWorld : IScenarioWorld<ClinicWorld>
{
    private ClinicWorld(TestIsolation isolation) => Isolation = isolation;

    public TestIsolation Isolation { get; }

    public static async ValueTask<ClinicWorld> CreateAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The seed is scenario metadata: [IsolationSeed(2102)] on the scenario, or else one derived
        // from the scenario's stable id, so it is the same on every run and every machine.
        var seed = context.Scenario.Method?.GetCustomAttribute<IsolationSeedAttribute>()?.Seed
            ?? StableSeed(context.Scenario.Id);

        var isolation = await TestIsolation.CreateAsync(seed, context.CancellationToken);
        context.OnTeardown(Cleanup.Required, teardown =>
        {
            teardown.Log($"released {isolation.Tenant}");
            return isolation.ReleaseAsync();
        });

        context.Log($"isolated in {isolation.Tenant}");
        context.SimulateElapsed(TimeSpan.FromMilliseconds(400));
        return new ClinicWorld(isolation);
    }

    private static int StableSeed(string scenarioId)
    {
        var hash = 2166136261u;
        foreach (var c in scenarioId)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % 100_000);
    }
}

// ---- Steps: plain classes, instance methods. A group is a property returning Steps<T>(). ----

public sealed class ClinicGiven : Given<ClinicWorld>
{
    public CustomerSteps Customers => Steps<CustomerSteps>();

    public BookingSteps Bookings => Steps<BookingSteps>();

    public StubSteps Stubs => Steps<StubSteps>();
}

public sealed class CustomerSteps : Given<ClinicWorld>
{
    [StepName("Given customer {name} exists")]
    public Task<Customer> Exists(string name)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(150));
        Context.Log($"created {name} in {World.Isolation.Tenant}");
        return Task.FromResult(new Customer(name));
    }
}

public sealed class BookingSteps : Given<ClinicWorld>
{
    [StepName("Given {customer} has a booking in {daysAhead} days")]
    public Task<Booking> Existing(Customer customer, int daysAhead)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(220));
        var booking = new Booking(
            $"B-{World.Isolation.Seed}-{customer.Name}",
            customer,
            DateOnly.FromDateTime(Context.TimeProvider.GetUtcNow().UtcDateTime).AddDays(daysAhead));
        Context.OnTeardown(teardown =>
        {
            teardown.Log($"deleted {booking.Id}");
            return Task.CompletedTask;
        });
        return Task.FromResult(booking);
    }
}

public sealed class StubSteps : Given<ClinicWorld>
{
    [StepName("Given the {service} service accepts calls")]
    public Task Accepts(string service)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(120));
        World.Isolation.Stubs.Accept(service);
        return Task.CompletedTask;
    }

    [StepName("Given the {service} service is down")]
    public Task IsDown(string service)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(90));
        World.Isolation.Stubs.Fail(service);
        return Task.CompletedTask;
    }
}

public sealed class ClinicWhen : When<ClinicWorld>
{
    public BookingActions Bookings => Steps<BookingActions>();
}

public sealed class BookingActions : When<ClinicWorld>
{
    [StepName("When {customer} cancels {booking}")]
    public Task<Cancellation> Cancel(Customer customer, Booking booking)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(300));
        if (booking.Customer != customer)
        {
            throw new InvalidOperationException($"{customer} cannot cancel {booking}");
        }

        World.Isolation.Stubs.Call("notifications", $"{booking.Id} cancelled");
        var calendarUpdated = World.Isolation.Stubs.Call("calendar", $"free {booking.Day:yyyy-MM-dd}");
        return Task.FromResult(new Cancellation(booking, calendarUpdated));
    }
}

public sealed class ClinicThen : Then<ClinicWorld>
{
    public NotificationChecks Notifications => Steps<NotificationChecks>();

    [StepName("Then {cancellation} is recorded")]
    public Task IsRecorded(Cancellation cancellation)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(80));
        Assert.StartsWith($"B-{World.Isolation.Seed}-", cancellation.Booking.Id, StringComparison.Ordinal);
        return Task.CompletedTask;
    }

    [StepName("Then the calendar {state}")]
    public Task Calendar(Cancellation cancellation, string state)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(60));
        Assert.Equal(state == "was updated", cancellation.CalendarUpdated);
        return Task.CompletedTask;
    }
}

public sealed class NotificationChecks : Then<ClinicWorld>
{
    [StepName("Then {customer} is told about {booking}")]
    public Task WereSent(Customer customer, Booking booking)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(70));
        Assert.Contains($"notifications: {booking.Id} cancelled", World.Isolation.Stubs.Calls);
        Context.Log($"{customer} notified");
        return Task.CompletedTask;
    }
}

/// <summary>A custom phase is a step class with its own label.</summary>
[PhaseName("Eventually")]
public sealed class ClinicEventually : Phase<ClinicWorld>
{
    [StepName("Eventually {booking}'s slot is free again")]
    public Task SlotIsFree(Booking booking)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(500));
        Assert.Contains($"calendar: free {booking.Day:yyyy-MM-dd}", World.Isolation.Stubs.Calls);
        return Task.CompletedTask;
    }
}

// ---- The wiring: once per suite. Go-to-definition on `Given` in a scenario lands here. ----

public abstract class ClinicScenarios : Scenarios<ClinicWorld>
{
    public ClinicGiven Given => Steps<ClinicGiven>();

    public ClinicWhen When => Steps<ClinicWhen>();

    public ClinicThen Then => Steps<ClinicThen>();

    public ClinicEventually Eventually => Steps<ClinicEventually>();
}

// ---- Scenarios: instance methods; results passed explicitly. ----

[DisplayName("Appointment cancellation")]
public sealed class CancellationScenarios : ClinicScenarios
{
    [Scenario("customer cancels a booking")]
    [IsolationSeed(2102)]
    public async Task CustomerCancels()
    {
        var customer = await Given.Customers.Exists("Jane");
        var booking = await Given.Bookings.Existing(customer, daysAhead: 10);
        await (Given.Stubs.Accepts("notifications"), Given.Stubs.Accepts("calendar"));

        var cancellation = await When.Bookings.Cancel(customer, booking);

        await (Then.IsRecorded(cancellation), Then.Notifications.WereSent(customer, booking));
        await Eventually.SlotIsFree(booking);
    }

    [Scenario("a cancellation is still recorded when the calendar is down")]
    public async Task CalendarDown()
    {
        var customer = await Given.Customers.Exists("Omar");
        var booking = await Given.Bookings.Existing(customer, daysAhead: 3);
        await (Given.Stubs.Accepts("notifications"), Given.Stubs.IsDown("calendar"));

        var cancellation = await When.Bookings.Cancel(customer, booking);

        await (Then.IsRecorded(cancellation), Then.Calendar(cancellation, "was not updated"));
    }
}
