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

Attach explanations to `Task` / `Task<T>` with `.Note("why")` or `.Note($"why {value}")`.
Notes remain in the step result, including skipped steps when their inputs are available.
Resource-aware `Context.Log` messages and `[StepName]` titles support `:id` and `:data`;
recognition uses `IResource<T>`, `IResourceIdentity` or a registered `Identify<T>` selector.

Step titles can link their returned resource with `{$return:id ?? name}`;
`name` remains visible until the returned value is available.

Resource types can use `[DisplayName("Appointment")]` for friendly report labels.
Every object reference captures its full raw JSON at that point in the scenario; hover
to inspect or copy it. `:id` keeps the text compact; `:data` explicitly adds a short inline preview.

The coverage/classification APIs are available in `Raun.Coverage` and `Raun.Mtp`.
The HTML report has Tests and Coverage views. Expand a step for its resource events;
open its logs when needed. Resource references highlight their usage; Esc clears the highlight.

```bash
dotnet add package Raun.Mtp --prerelease      # .NET 10 SDK 10.0.300+
dotnet run -- --report-html
```

**Where to look next**
- `samples/AppointmentTests` has every feature in working form.
- Design notes are in `docs/superpowers/specs/`.
- [Releasing](docs/RELEASING.md).

Apache 2.0.
