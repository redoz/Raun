# Custom resource actions and cross-step labels

Date: 2026-10-16
Status: implemented; full verification and final Astra High review complete.
Execution preference: implement here with one final reviewer, as previously selected.
Release policy: no tag or publication unless requested.

## Intent

Consumers define a domain action once, then use its attribute on steps. The attribute determines its
base effect and report label. Consumers do not maintain registration tables or repeat mappings at
call sites. ResourceKey<T> strings must reuse a useful label learned from an earlier step for the
same canonical resource. Attribute XML comments must explain the effects in ASD-STE100 style.

## Custom attributes

Make CreatedAttribute, LoadedAttribute, ReadAttribute, EditedAttribute, and DeletedAttribute
extensible. Recognize their exact Raun base symbols through any number of derived classes, including
classes in referenced libraries. Names alone do not establish semantics.

```csharp
[System.ComponentModel.DisplayName("Approve")]
public sealed class ApprovedAttribute : EditedAttribute;

public Task Approve([Approved] Patient patient);

public sealed class RegisteredAttribute : CreatedAttribute;
public Task Register([Registered] Patient patient);
```

The first attribute's action label is Approve and its effect is Edit. Without DisplayName, its label
is Approved: use the concrete attribute type name and remove only a terminal Attribute suffix.
A nearest explicit DisplayName on the custom attribute hierarchy overrides that default. Reject
blank explicit labels. Preserve existing built-in report labels Create, Load, Read, Edit, Delete,
Reference, and Consume; the custom-name default does not rename built-in effects.

Use symbol metadata during generation and CustomAttributeData for runtime action metadata. Never
execute a custom attribute constructor, property getter, or static initializer to discover its label
or semantics. Constructor code cannot change a custom action's base effect. Inherited References and
Consumes are configured with named arguments at the usage, as today; do not evaluate constructor
assignments to infer these arrays.

## Base effects and locations

| Effect | Meaning | Access | Attribute base |
| --- | --- | --- | --- |
| Create | The step creates a new resource. | Exclusive | CreatedAttribute |
| Load | The step gets an existing resource. | Shared | LoadedAttribute |
| Read | The step uses an existing resource without a change. | Shared | ReadAttribute |
| Edit | The step changes an existing resource. | Exclusive | EditedAttribute |
| Delete | The step removes an existing resource. | Exclusive | DeletedAttribute |
| Reference | The produced resource keeps a reference to another resource. | Shared | Existing References relation |
| Consume | Another resource is an input to the produced resource. | Shared | Existing Consumes relation |

Keep the seven LifecycleVerb values and existing conflict/dedup precedence. Reference and Consume
remain lineage relations with explicit endpoints; this feature does not introduce standalone
relation attributes. Consume does not imply Delete or exclusive access. An operation that removes
an input must also declare its Delete effect.

Allow the five primary role attributes on a parameter, a return value, or a method. A method-level
role is shorthand for its returned resource and requires a supported value-returning step. This
allows Create on input data and Delete on a returned resource. Keep return-role override precedence
for an explicit return attribute versus method shorthand. The selected attribute supplies the complete
action and lineage; an empty return lineage does not inherit method lineage. Diagnose multiple competing role
attributes on a single subject instead of silently selecting the first. A method can declare
several effects across different parameters and its return value.

Preserve current effect timing: parameter claims are recorded before the call, so known conflicts
are rejected before its operation. Claims that require the return value follow the call. Effects
are declarations of access, not proof that the operation completed; the step result records success
or failure. Do not move claims after calls to accommodate custom labels. Support key-only parameters
for all five effects without recording a ResourceIdentity wrapper as object data. Add explicit
Create(ResourceIdentity) and Load(ResourceIdentity) overloads where required for those paths.

## Runtime and report representation

Separate the base effect from the domain action. An immutable ResourceAction contains the base
LifecycleVerb, display name, and optional custom attribute Type. The attribute Type distinguishes
custom actions with identical display names. Existing effects without custom metadata retain their
current interpretation. No newly required generated-model properties.

Normal users apply the custom attribute; the generator emits its resolved descriptor. For imperative
steps, provide a metadata-only factory and a typed resource overload:

```csharp
var approve = ResourceAction.For<ApprovedAttribute>();
await Context.Resources.Apply(approve, patient);
```

Apply infers the resource's declared type from its value, preserving alternate key/label semantics.
Also support Apply(action, ResourceIdentity) for a key-only effect. The factory derives effect and
label from the same attribute hierarchy, caches immutable metadata, and requires no registration.
An unsupported attribute produces an actionable error. Existing lifecycle methods remain available.
Reference/Consume retain their existing imperative APIs and explicit endpoint handling.

The ledger always uses the base effect and canonical resource identity. Custom labels cannot weaken
access checks or define new precedence. Preserve built-in per-step effect aggregation. Within a step,
coalesce repeated observations of the same custom action and identity; retain distinct custom
actions as distinct effect entries. Each resource still has one report identity. Chronological logs
retain every observation. Repeated entries keep the first timestamp, strongest applicable effect,
and latest non-null payload, as existing aggregation does.

Reports carry semantic effect separately from display action. Existing Verb fields retain their
semantic values for compatibility; add optional action-name metadata. Use the action name as visible
text. Use the base effect for styling, filters that describe access, conflict messages, and lineage
behavior. Escape custom labels as text and preserve the existing resource-matrix geometry and effect-table alignment. A
filter or legend that lists action names must include custom names without a hard-coded enumeration.

## Remembered resource labels

Use explicit scenario-owned presentation state, shared by titles, notes, logs, resource effects, and
teardown. Do not store observed labels on the DI-supplied ResourceIdentityResolver, a static cache,
or the generated manifest. A shared resolver must not transfer labels between scenarios or runs.

Index observations by canonical ResourceIdentity (Kind + Key). Domain objects, alternate DTOs, and
key-only references therefore share names. Keep the frozen label string and producer location;
do not keep the source object or create another snapshot store.

When an object is observed, try the current source label chain: registered Label<TSource>, alternate
LabelFor, then source self LabelFor. A nonblank result is meaningful evidence and can update the
remembered label. If this observation has no meaningful label, try eligible remembered evidence,
then the existing kind/key display. A fallback never replaces a meaningful stored name. Do not infer
label quality by comparing its text with the fallback string.

When only a key or ResourceIdentity is available, use remembered evidence before kind/key display.
Do not invoke an object-based label selector without an instance. Keep :id as the raw key. Key-only
:data and hover behavior remain data-free; a remembered name is not an object snapshot.

Names follow scenario ordering, not thread completion races. A step can see its own earlier label
observations and observations from dependency predecessors. Use the existing ScenarioGraph ordering,
including merge, guard, and ordering-only edges. An unrelated parallel step cannot change the name
used in a title. Among visible producers, prefer causally newer observations; if independent producer
branches both precede the reader, use declaration/node order as a deterministic tie-break. Within one
step, use observation order. These rules apply to skipped-step formatting and teardown as well. On host cancellation, request
cancellation promptly but capture pending-node skipped presentation only after relevant predecessors
drain. Do not publish a provisional title and replace it later.

Labels are evidence, not proof of resource creation or successful mutation. A name actually observed
in a failed predecessor can still name that resource in subsequent skipped-step output. Do not infer
unknown values from skipped calls. Previously captured titles, notes, logs, and effects retain their
labels and snapshots. A later good label does not rewrite history or a running step's captured title.

## ASD-STE100-style XML comments

Use short active sentences, one meaning per term, and defined software technical nouns. Use the same
words for the same concepts across all five attributes and the lineage properties. Avoid CRTP,
aggregation/composition terminology, synonyms for the base effects, and unexplained abbreviations
in these public comments. Follow the official writing rules and controlled-vocabulary guidance;
review terms in the software domain rather than treating a sentence-length check as conformance.

Sources: [official STE overview](https://www.asd-ste100.org/about_STE.html) and
[official STE FAQ](https://www.asd-ste100.org/STE_faq.html). Use the standard for the terminology
review; do not infer full conformance from a sentence-length or automated readability check.

Each role summary gives its meaning. Remarks state valid locations, access behavior, parameter versus
return recording time, and how a derived attribute selects a report label. Define shared and exclusive
access plainly: shared access permits unordered readers; exclusive access conflicts with any
unordered use of the same resource. Explain that Raun reports conflicts; it does not lock external
systems or perform the step's operation.

Illustrative CreatedAttribute comment (the implementation applies the same style to all roles):

```csharp
/// <summary>The step creates a new resource.</summary>
/// <remarks>
/// <para>Use this attribute on a parameter, a return value, or a method.</para>
/// <para>An attribute on a method applies to its return value.</para>
/// <para>This effect requires exclusive access to the resource.</para>
/// <para>Raun reports a conflict if an unordered step uses the same resource.</para>
/// <para>The step code must create the resource.</para>
/// <para>A derived attribute can give this effect a different report label.</para>
/// </remarks>
```

Lineage property comments explain direction, valid targets, and access. For Consumes, explicitly state:
"This relation does not delete the input resource." For References, state that the target resource
has a separate identity. Use nameof(parameter) and Subject.Return examples to explain target names.
Include the custom DisplayName override and terminal Attribute-suffix default in relevant remarks.

## Alternatives considered

- Free text at every call site: rejected because names and access mappings would drift.
- Registration tables: rejected because each custom attribute already defines its base effect.
- Infer semantics from the action name: rejected; a label cannot determine whether data changes.
- New arbitrary effect/precedence values: rejected; the current base effects cover the intended uses.
- A process-wide label dictionary: rejected because canonical keys can repeat in different scenarios.
- Keep live objects and call LabelFor again for a key: rejected; it retains mutable data and rewrites evidence.
- Let the last parallel completion choose a label: rejected because report names would vary with timing.
- Replace earlier labels after the run: rejected because titles/logs must preserve recording-time evidence.

## Validation and documentation

Tests first. Cover direct and indirect custom attributes, referenced-library definitions, DisplayName
inheritance/defaults, same-name custom actions, unsupported or ambiguous attributes, widened locations,
key-only parameters, static and runtime base-effect recognition, and discovery without user code.
Assert that custom Edit actions conflict exactly like Edit, and custom Read actions retain shared
access. Pin same-step aggregation for built-in and custom actions, multiple resources per operation,
recording order, and unchanged argument evaluation/branch/merge/cleanup behavior.

Cover good label followed by a key, alternate representations of the same resource, blank/fallback
observations, an updated meaningful label, same key on different kinds, and no cross-scenario leakage
with a shared DI resolver. Prove deterministic ordering across parallel branches, original snapshot
retention, :id/:data behavior, skipped steps, teardown, and repeated scenario runs.

README examples must show a named custom action and the default name, its inherited base effect,
parameter/return usage, imperative Apply, and automatic remembered labels. Review the public XML
comments for precise meaning and STE style. Run locked restore, zero-warning solution build, full
solution tests, report checks, resource-matrix browser checks, and local package validation. Obtain one final staff review, fix
substantive findings, and commit/push. Do not publish a release.

## Implementation outcome — 2026-10-16

The approved design is implemented, including the five plan-review corrections. The final staff
review's collectible-cache and empty-default-name findings were reproduced and fixed. Action metadata
uses a weak-key cache so discarded assembly load contexts can collect. Runtime and analyzer validation
both reject a final empty action name.

Locked restore, a zero-warning Release build, all 1,526 solution results (1,521 passed and five expected
branch skips), 72 report JavaScript checks, the resource-matrix browser harness, and four local package
builds passed. README and the canonical sample show named/default custom attributes and a key-only
follow-up with a remembered label and no borrowed snapshot. No release was published.
