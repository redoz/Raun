# Raun

Given/When/Then scenario tests for .NET. Each step is reported as its own test; results flow
between steps; a failure skips only what depends on it.

```csharp
[Scenario("customer books an appointment")]
public async Task Booking()
{
    var (patient, slot) = await (Given.PatientExists("Jane"), Given.AvailableSlot());   // parallel

    switch (await When.CreateAppointment(patient, slot))                                // branch
    {
        case Booked booked: await Then.AppointmentExists(booked.Appointment); break;
        case Waitlisted:    await Then.PatientIsWaitlisted(patient); break;
    }
}
```

A source generator turns each scenario into a step graph at compile time. Raun runs that graph on
Microsoft.Testing.Platform.

```bash
dotnet add package Raun.Mtp --prerelease      # .NET 10 SDK 10.0.300+
dotnet run -- --report-html
```

**Where to look next**
- `samples/AppointmentTests` has every feature in working form.
- Design notes are in `docs/superpowers/specs/`.
- [Releasing](docs/RELEASING.md).

Apache 2.0.
