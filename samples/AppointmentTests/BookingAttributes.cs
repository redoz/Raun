using System.ComponentModel;
using Raun;

namespace AppointmentTests;

/// <summary>The step registers a patient.</summary>
[DisplayName("Register")]
public sealed class RegisteredAttribute : CreatedAttribute;

/// <summary>The step holds a slot.</summary>
[DisplayName("Hold")]
public sealed class HeldAttribute : EditedAttribute;

/// <summary>The step cancels a booking.</summary>
[DisplayName("Cancel")]
public sealed class CancelledAttribute : DeletedAttribute;

/// <summary>The step creates a booking linked to a patient and a slot.</summary>
[ResourceActionDefinition(ResourceActionKind.Create)]
public sealed class BookedAttribute : ResourceAttribute
{
    /// <summary>The step parameter that supplies the patient. The booking keeps a link to it.</summary>
    [Relationship<Patient>(ResourceRelation.Reference)]
    public string Patient { get; set; } = "";

    /// <summary>The step parameter that supplies the slot. This input is not deleted.</summary>
    [Relationship<Slot>(ResourceRelation.Consume)]
    public string Slot { get; set; } = "";
}

/// <summary>The step creates a replacement booking from a new slot.</summary>
[ResourceActionDefinition(ResourceActionKind.Create)]
public sealed class RescheduledAttribute : ResourceAttribute
{
    /// <summary>The step parameter that supplies the replacement slot. This input is not deleted.</summary>
    [Relationship<Slot>(ResourceRelation.Consume)]
    public string Slot { get; set; } = "";
}
