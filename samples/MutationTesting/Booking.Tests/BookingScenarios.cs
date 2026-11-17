using Booking;
using Raun;
using Xunit;

namespace Booking.Tests;

public sealed class BookingScenarios : Scenarios<NoWorld>
{
    public OpeningGiven Given => Steps<OpeningGiven>();
    public BookingWhen When => Steps<BookingWhen>();
    public BookingThen Then => Steps<BookingThen>();

    [Scenario("opening hours include 08:00 and exclude 18:00")]
    public async Task OpeningHours()
    {
        var early = await When.CheckHour(7);
        var opening = await When.CheckHour(8);
        var closing = await When.CheckHour(17);
        var late = await When.CheckHour(18);
        await Then.OnlyTheBusinessHoursAreOpen(early, opening, closing, late);
    }

    [Scenario("a slot is full once its capacity is reached")]
    public async Task Capacity()
    {
        var slot = await Given.SlotWithCapacity(9, 3);
        var oneFree = await When.CheckRoom(slot, 2);
        var full = await When.CheckRoom(slot, 3);
        await Then.OnlyTheNearlyFullSlotHasRoom(oneFree, full);
        await When.Describe(slot);
    }

    [Scenario("fees are charged per started half hour")]
    public async Task Fees()
    {
        var half = await When.PriceOf(30, false);
        var started = await When.PriceOf(31, false);
        var hour = await When.PriceOf(60, false);
        var memberHalf = await When.PriceOf(30, true);
        var memberStarted = await When.PriceOf(31, true);
        var memberHour = await When.PriceOf(60, true);
        await Then.FeesMatchTheTariff(half, started, hour, memberHalf, memberStarted, memberHour);
    }
}

public sealed class OpeningGiven : Given<NoWorld>
{
    [StepName("Given a {capacity}-seat slot at {hour}:00")]
    public Task<Slot> SlotWithCapacity(int hour, int capacity) => Task.FromResult(new Slot(hour, capacity));
}

public sealed class BookingWhen : When<NoWorld>
{
    [StepName("When {hour}:00 is checked against opening hours")]
    public Task<bool> CheckHour(int hour) => Task.FromResult(BookingRules.IsWithinOpeningHours(hour));

    [StepName("When {booked} bookings are checked against the slot")]
    public Task<bool> CheckRoom(Slot slot, int booked) => Task.FromResult(BookingRules.HasRoom(slot, booked));

    [StepName("When a {minutes} minute session is priced")]
    public Task<decimal> PriceOf(int minutes, bool isMember) => Task.FromResult(BookingRules.Fee(minutes, isMember));

    [StepName("When the slot is described")]
    public Task Describe(Slot slot)
    {
        Context.Log(BookingRules.Describe(slot));
        return Task.CompletedTask;
    }
}

public sealed class BookingThen : Then<NoWorld>
{
    [StepName("Then only 08:00 to 17:00 are open")]
    public Task OnlyTheBusinessHoursAreOpen(bool early, bool opening, bool closing, bool late)
    {
        Assert.False(early);
        Assert.True(opening);
        Assert.True(closing);
        Assert.False(late);
        return Task.CompletedTask;
    }

    [StepName("Then only the slot with a seat left has room")]
    public Task OnlyTheNearlyFullSlotHasRoom(bool oneFree, bool full)
    {
        Assert.True(oneFree);
        Assert.False(full);
        return Task.CompletedTask;
    }

    [StepName("Then fees follow the tariff")]
    public Task FeesMatchTheTariff(decimal half, decimal started, decimal hour, decimal memberHalf, decimal memberStarted, decimal memberHour)
    {
        Assert.Equal(50m, half);
        Assert.Equal(100m, started);
        Assert.Equal(100m, hour);
        Assert.Equal(40m, memberHalf);
        Assert.Equal(80m, memberStarted);
        Assert.Equal(80m, memberHour);
        return Task.CompletedTask;
    }
}
