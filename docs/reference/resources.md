# Resources

[Start here](../../README.md) · [Writing scenarios](scenario-authoring.md) · [Running tests](running-tests.md)

## Resource keys, labels, and snapshots

A **key** identifies a resource. A **label** supplies readable text. A **snapshot** captures data at a reference.

```csharp
public sealed record Patient(string Id, string Name, string? City = null) : IResource<Patient>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Id;
    public static string? LabelFor(Patient patient) => patient.Name;
}

public sealed record Slot(int Id) : IResource<Slot>
{
    public static ResourceKey KeyFor(Slot slot) => slot.Id.ToString(CultureInfo.InvariantCulture);
}

public sealed record Appointment(Patient Patient, Slot Slot) : IResource<Appointment>
{
    public static ResourceKey KeyFor(Appointment appointment) => $"{appointment.Patient.Id}@{appointment.Slot.Id}";
    public static string? LabelFor(Appointment appointment) => $"{appointment.Patient.Name} / slot {appointment.Slot.Id}";
}
```

Keys contain strings (`ResourceKey` wraps a string); the patient example keeps its stable ID separate from its display name.

`LabelFor` is optional. Labels never change resource identity. Explicit `Label<T>` selectors take
priority, followed by an alternate's label contract and the source type's own `LabelFor`.
If none supplies a name, Raun uses a remembered name visible to this step, then `DisplayName:key`.
Null or blank labels use the next option. `[DisplayName("Patient")]` names the resource kind.
Raun does not call `ToString()` to choose a label. See [names across steps](#names-across-steps).

| Format in a title, interpolated log, or note | Text |
| --- | --- |
| `{patient}` | Label |
| `{patient:id}` | Key |
| `{patient:data}` | Inline data preview |

Object references keep their resource link and captured JSON when serialization succeeds.
Key-only references keep the identity and link, with no object snapshot. Hover over an object reference
to inspect or copy its snapshot.
Labels and snapshots are captured at the reference; later changes do not rewrite them.
Use synthetic test data: Raun does not redact reports or artifacts.

## Alternate resource representations

A domain object, EF row, and API response can represent the same patient. Declare their keys on the
resource owner; the external types need no changes, mapping attributes, or startup configuration:

```csharp
public sealed record Patient(string Id, string Name) :
    IResource<Patient>,
    IResourceAlternate<PatientRow>,
    IResourceAlternate<PatientResponse>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Id;
    public static string? LabelFor(Patient patient) => patient.Name;

    static ResourceKey IResourceAlternate<PatientRow>.KeyFor(PatientRow row) => row.Id;
    static string? IResourceAlternate<PatientRow>.LabelFor(PatientRow row) => row.Name;
    static ResourceKey IResourceAlternate<PatientResponse>.KeyFor(PatientResponse response) => response.PatientId;
    static string? IResourceAlternate<PatientResponse>.LabelFor(PatientResponse response) => response.DisplayName;
}
```

All three resolve to **Patient + key**. Their resource roles share conflict detection, lineage, and
report links. `[Read, ResourceKey<Patient>] string patientId` and `Context.Resources.Read<PatientRow>(key)`
refer to that same identity. Keys returned by each extractor must agree; key-only references already
contain the canonical key and cannot transform it without an object. Distinct resource kinds can
use the same key without colliding.

If there is no domain object, use an empty owner with only alternate contracts:

```csharp
public sealed class PatientResource : IResourceAlternate<PatientRow>
{
    public static ResourceKey KeyFor(PatientRow row) => row.Id;
    public static string? LabelFor(PatientRow row) => row.Name; // optional
}
```

The owner is never instantiated and needs no `KeyFor(PatientResource)`. Its key-only parameters use
`ResourceKey<PatientResource>`. A type implementing only the non-generic `IResource` marker can also
name a key-only kind.

Raun's generator discovers closed, accessible contracts, including owners in referenced libraries
built with the Raun generator. A mapping library can contain no scenarios, and its owner can be
internal. Conflicting owners for the same representation are compile errors. Labels use the declared
source representation; snapshots serialize the original runtime object. A row's label method never
receives a domain object, and later response data never rewrites earlier title or log snapshots.

For a custom host, configure mappings in Program.cs:

```csharp
var identities = new ResourceIdentityResolver();
identities.Identify<Patient>()
    .With<PatientRow>(row => row.Id)
    .With<PatientResponse>(response => response.PatientId);
identities.Label<PatientRow>(row => row.Name);

// Or a named kind without a CLR owner:
// identities.Identify("patient").With<Patient>().With<PatientRow>(row => row.Id);

await using var services = new ServiceCollection().AddSingleton(identities).BuildServiceProvider();
return await RaunTestApplication.RunAsync(args, services: services);
```

Put this in your `Program.cs` with `<RaunGenerateProgram>false</RaunGenerateProgram>` and
`using Microsoft.Extensions.DependencyInjection;`. In an Aspire host, register the configured resolver
inside `aspire.Services(services => services.AddSingleton(identities))` in the `RaunAspire.RunAsync`
configuration callback. The generated host needs neither change.

`With<T>()` reuses `IResource<T>.KeyFor`; `With<T>(selector)` supplies an explicit mapped extractor.
Matching generated defaults preserve explicit fluent extractors and label selectors. The older
`Identify<T>(selector)` registers an unmapped selector below the type's own `IResource<T>.KeyFor`.
It remains available through `Context.Resources`, alongside `Label<T>`.

Configure groups and mapped extractors before the first successful identity resolution; they then
freeze so stored claims and references stay consistent. Reapplying identical defaults is harmless.
Typed groups merge, named kinds win over typed kinds, and distinct named groups cannot merge.
Named kinds use ordinal, case-sensitive names. `ResourceIdentity.Kind` covers both forms;
its `Type` property is null for named kinds.

## Effects and access

Declare the operation performed on each resource. Raun records parameter effects before the call,
and return effects after a successful call. A method attribute describes its return value and requires
`Task<T>` or `ValueTask<T>`; an explicit return attribute takes precedence.

| Effect | Built-in attribute | Access |
| --- | --- | --- |
| Create | `[Created]` | Exclusive |
| Read | `[Read]` | Shared |
| Update | `[Updated]` | Exclusive |
| Delete | `[Deleted]` | Exclusive |

`[Seed]` is a Create action named Seed. Use it for fixture data inserted behind the scenes. Use
Created or a domain action such as Registered for creation through the system under test.
Attributes record operations; the step code must perform them. There is no separate Load effect.

Parallel steps can share Read access. Unordered accesses to the same identity conflict when either
is exclusive. The compiler detects known conflicts; runtime key checks catch the rest. A runtime
conflict fails the step without waiting. These step claims are separate from scenario admission.
`[Uses<T>(LockMode.Exclusive)]` can hold an external system for the entire scenario, including setup,
teardown, and scope disposal. Existing Shared, Exclusive, and Pooled admission policies still apply.

Use the imperative `Context.Resources.Create`, `Read`, `Update`, `Delete`, or `Apply` methods for
operations inside a step. `Read<Patient>(key)` records a key-only Read without a snapshot.

## Reusable actions and typed links

A reusable attribute inherits a built-in action's effect. Its default label is the class name with
`Attribute` removed. `System.ComponentModel.DisplayName` overrides it; blank labels are errors.
Raun reads metadata without constructing attributes or executing their code.

```csharp
[DisplayName("Approve")]
public sealed class Approved : UpdatedAttribute;

[DisplayName("Reserve")]
public sealed class Reserved : UpdatedAttribute;

public sealed class Booked : CreatedAttribute
{
    public sealed class PatientAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
    public sealed class SlotAttribute : ResourceLinkAttribute<Slot, Reserved>;
}

[StepName("When booking {patient} in {slot2}")]
[return: Booked]
public Task<Appointment> Book([Booked.Patient] Patient patient, [Booked.Slot] Slot slot2) { ... }
```

This records Read on the patient, Reserve/Update on the slot, and Booked/Create on the returned
appointment. It also records two links: booking → Patient → patient and booking → Slot → slot.
A link is an independent `ResourceLink` with `Source`, `Role`, and `Target`; it adds no access claim
or resource effect. Two roles can connect the same identities. Exact duplicate links are recorded once.

Declare role attributes as public concrete nested types inside the action. Each role identifies a
canonical resource type and an action to apply to the input. All roles are required and each binds to
one parameter. A supported resource collection parameter records one action and link per element.
Mapped representations of the canonical resource satisfy the same role. Role names come from the
nested type name with `Attribute` removed; a parameter can have any name.

The equivalent explicit form separates the action and link:

```csharp
[return: Booked]
public Task<Appointment> Book(
    [Read, Link<Booked>("Patient")] Patient patient,
    [Reserved, Link<Booked>("Slot")] Slot slot2) { ... }
```

A raw link always requires a separate input action. If the step applies the same owning action more
than once, set `Source = nameof(ownerParameter)` or `Source = Subject.Return` on the raw link.
Raun reports missing, duplicate, ambiguous, or incompatible bindings at compile time. A failed return
creates no return-owned link. Self-links are rejected after canonical identity resolution.

For imperative code:

```csharp
await Context.Resources.Apply(ResourceAction.For<Approved>(), patient);
await Context.Resources.Apply(ResourceAction.For<Booked>(), appointment);
await Context.Resources.Link(ResourceAction.For<Booked>(), "Patient", appointment, patient);
// A schema-free named link is also available. It grants no access:
await Context.Resources.Link("Patient", appointment, patient);
```

Distinct custom action types retain separate observations, even if their labels match. Repeated
explicit action calls record observation events; link calls do not repeat action logs or snapshots.
See the [domain attributes](../../samples/AppointmentTests/BookingAttributes.cs) and
[action sample](../../samples/AppointmentTests/ResourceActionScenarios.cs).

## Key-only inputs

Annotate a `string` or `ResourceKey` parameter with `[ResourceKey<TResource>]` and an action or packaged role:

```csharp
public Task Delete([Deleted, ResourceKey<Patient>] string patientId) { ... }

[return: Booked]
public Task<Appointment> Book(
    [Booked.Patient, ResourceKey<Patient>] string patientId,
    [Booked.Slot] Slot slot) { ... }
```

The method receives its original value. Effects, links, conflicts, and title references resolve to
the same identity as an object with that key. Key-only references have no object snapshot and can reuse
a readable name observed by a preceding dependency. A missing role is a compile error; other key
parameter types report `RAUN024`. A supplied null key fails before recording its claim.
For a manual log or note, format `new ResourceIdentity(typeof(Patient), patientId)`.
See the [registry sample](../../samples/AppointmentTests/RegistryBookingScenarios.cs).

## Names across steps

A label from `Label<T>`, an alternate representation's `LabelFor`, or `IResource<T>.LabelFor`
is remembered for that canonical resource identity during the scenario. Later key-only references
reuse it in titles, notes, logs, and resource effects. A blank label or a selector that throws
cannot erase a good name. Different representations of the same identity share that name;
different resource kinds and scenario cases keep separate names.
`:id` still shows the key; key-only references never borrow a previous object's snapshot.

A step can see its own earlier observations and its graph predecessors. A causally newer name wins.
When independent predecessor branches meet, the later node in scenario order wins, regardless of
completion order. Unrelated parallel steps and separate scenario runs do not share observations.
Cleanup sees the drained scenario's names. Captured titles, logs, and snapshots stay frozen.

