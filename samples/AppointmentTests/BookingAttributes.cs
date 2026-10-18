global using AppointmentTests.Actions;
using System.ComponentModel;
using Raun;

namespace AppointmentTests.Actions;

/// <summary>Record a patient registration. The step must create the patient through the system.</summary>
[DisplayName("Register")]
public sealed class Registered : CreatedAttribute;

/// <summary>Record a temporary hold on a slot. This action requires exclusive access.</summary>
[DisplayName("Hold")]
public sealed class Held : UpdatedAttribute;

/// <summary>Record a slot reservation. This action changes the slot and requires exclusive access.</summary>
[DisplayName("Reserve")]
public sealed class Reserved : UpdatedAttribute;

/// <summary>Record a booking cancellation. The step must delete the booking.</summary>
[DisplayName("Cancel")]
public sealed class Cancelled : DeletedAttribute;

/// <summary>Record a new booking with its patient and reserved slot.</summary>
public sealed class Booked : CreatedAttribute
{
    /// <summary>Read the patient and link the booking to that patient.</summary>
    public sealed class PatientAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
    /// <summary>Reserve the slot and link the booking to that slot.</summary>
    public sealed class SlotAttribute : ResourceLinkAttribute<Slot, Reserved>;
}

/// <summary>Record a replacement booking with its reserved slot.</summary>
public sealed class Rescheduled : CreatedAttribute
{
    /// <summary>Reserve the replacement slot and link the booking to that slot.</summary>
    public sealed class SlotAttribute : ResourceLinkAttribute<Slot, Reserved>;
}
