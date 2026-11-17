namespace Booking;

public sealed record Slot(int StartHour, int Capacity);

public static class BookingRules
{
    // Open 08:00 inclusive to 18:00 exclusive.
    public static bool IsWithinOpeningHours(int hour) => hour >= 8 && hour < 18;

    public static bool HasRoom(Slot slot, int alreadyBooked) => alreadyBooked < slot.Capacity;

    // 50 per started half hour; members pay 80%.
    public static decimal Fee(int minutes, bool isMember)
    {
        var halfHours = (minutes + 29) / 30;
        var fee = halfHours * 50m;
        return isMember ? fee * 0.8m : fee;
    }

    // Called by a scenario, never asserted on.
    public static string Describe(Slot slot) => $"slot at {slot.StartHour}:00 for {slot.Capacity}"; // stryker-sentinel
}
