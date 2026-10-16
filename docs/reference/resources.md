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
priority, followed by an alternate's label contract, the source type's own `LabelFor`, and finally
`DisplayName:key`. Null or blank labels use the next option. `[DisplayName("Patient")]` names the
resource kind. Raun does not call `ToString()` to choose a label.

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
report links. `[Read, ResourceKey<Patient>] string patientId` and `Context.Resources.Load<PatientRow>(key)`
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

## Resource roles and conflicts

Declare each step's resource effects. The report shows them and the links between resources.
Resource keys dispatch through `IResource<T>.KeyFor`, including explicit static interface implementations,
so object and key-only references share the same identity.

```csharp
[StepName("When booking for {patient}")]
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
public Task<Appointment> CreateAppointment(Patient patient, Slot slot) { ... }
```

Use `[Created]`, `[Loaded]`, or `[Edited]` on returns; use `[Read]`, `[Edited]`, or `[Deleted]` on parameters.
`References` records retained links. `Consumes` records inputs used to produce the result.
Missing resource roles cause compiler diagnostics.
For effects inside a step, use `await Context.Resources.Create(value)` or `Load`, `Read`, `Edit`, `Delete`, `Reference`, and `Consume`.

Parallel steps must not change the same resource. Raun detects conflicts at compile time where possible, and at run time by key.
A run-time conflict fails the step; it does not wait for a lock.

When a step takes a resource's key instead of the object, annotate the parameter with
`[ResourceKey<TResource>]`. Combine it with the same access role:

```csharp
[StepName("delete {patientId}")]
public Task Delete([Deleted, ResourceKey<Patient>] string patientId)
    => DeletePatientById(patientId);
```

The string is the key of `Patient`, so its effects, conflicts, lineage, and title links refer to the
same resource as a `Patient` instance with that key. Key-only references display `Patient:key` and
carry no object snapshot. `ResourceKey` parameters are also supported. The annotation requires an
access role (or a producer's `References`/`Consumes` declaration); other parameter types report `RAUN024`.
The step method still receives its original string or `ResourceKey`; Raun changes only the tracking and title reference.
Nullable string parameters work when their supplied key is non-null. A supplied null key throws
`ArgumentNullException` when its claim is evaluated; no claim is recorded for that invalid key.
For a key-only reference in a manual log or note, format `new ResourceIdentity(typeof(Patient), patientId)`.

## Reusable resource actions

Derive a sealed attribute from one of the five resource roles to name a domain action once:

```csharp
[DisplayName("Approve")]
public sealed class ApprovedAttribute : EditedAttribute;

public sealed class AuditedAttribute : ReadAttribute; // Displays "Audited".

[StepName("approve {patientId}")]
public Task Approve([Approved, ResourceKey<Patient>] string patientId) { ... }

[Audited] // Method shorthand: records the returned resource after the call.
public Task<Patient> Audit(Patient patient) { ... }

await Context.Resources.Apply(ResourceAction.For<ApprovedAttribute>(), patient);
```

Use `System.ComponentModel.DisplayName` for a custom label. Otherwise Raun removes the final
`Attribute` suffix from the concrete attribute's name. The nearest inherited `DisplayName` wins;
a blank name is an error. Raun reads metadata without constructing the attribute.

An action keeps its base role's behavior: Created, Edited, and Deleted are exclusive;
Loaded and Read are shared. Input effects are recorded before the call; return effects after it.
Created, Loaded, and Edited also support inherited `References` and `Consumes` declarations.
Distinct custom attribute types retain separate report entries, even when their labels match.
The step code performs the operation; the attribute records its effect.

## Names across steps

A label from `Label<T>`, an alternate representation's `LabelFor`, or `IResource<T>.LabelFor`
is remembered for that canonical resource identity during the scenario. Later key-only references
reuse it in titles, notes, logs, and resource effects. A blank label cannot erase a good name.
`:id` still shows the key; key-only references never borrow a previous object's snapshot.

A step can see its own earlier observations and its graph predecessors. A causally newer name wins.
When independent predecessor branches meet, the later node in scenario order wins, regardless of
completion order. Unrelated parallel steps and separate scenario runs do not share observations.
Cleanup sees the drained scenario's names. Captured titles, logs, and snapshots stay frozen.

