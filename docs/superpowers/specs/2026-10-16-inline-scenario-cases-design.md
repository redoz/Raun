# Inline scenario cases

Date: 2026-10-16
Status: Approved by the user on 2026-10-16; implementation plan awaiting review.

## Goal

Run one scenario body against explicitly named constant inputs, initially for authorization checks
across endpoints and actors. Every case must be independently discovered, filtered, executed and
reported. Preserve the current behavior and identities of parameterless scenarios.

## Authoring contract

```csharp
[Scenario("endpoint authorization")]
[ScenarioCase("anonymous", "/orders", Role.Anonymous, HttpStatusCode.Unauthorized)]
[ScenarioCase("reader", "/orders", Role.Reader, HttpStatusCode.OK)]
public async Task Authorization(string endpoint, Role role, HttpStatusCode expected)
{
    var client = await Given.ClientFor(role);
    var response = await When.Get(client, endpoint);
    await Then.StatusIs(response, expected);
}
```

Add one ordinary, repeatable Raun.ScenarioCaseAttribute on methods. Its constructor is
`ScenarioCaseAttribute(string caseName, params object?[] arguments)`. Expose the case name and
read-only arguments for metadata inspection. ScenarioAttribute remains the scenario marker;
no per-method attribute generation is involved.

The first argument is an explicit stable case key and human-readable case name. It must be
nonblank and unique within the method under ordinal, case-sensitive comparison. Do not trim,
normalize, number or otherwise derive identity from it. The remaining positional arguments bind
to scenario parameters in declaration order. Require an exact argument count.

The initial value set is primitive attribute constants, strings, enum members and null. Apply
ordinary implicit C# argument conversion rules, including representable numeric constants,
nullable value types and boxing. Do not silently coerce an unrelated enum or a string to an enum.
Reject arrays, typeof values and arbitrary constructed/runtime values for this first version.
The compiler already rejects expressions that are not valid attribute arguments.

Normalize a null params-array metadata value to a single null case value when authored as the
second positional null argument. This lets `[ScenarioCase("missing", null)]` express a single
nullable argument naturally; parameter arity validation still applies. Explicit array expressions
as a case argument list are unsupported, avoiding competing flattened/nested array meanings.

Parameterized scenarios require at least one valid case. Case attributes require ScenarioAttribute
and a method with parameters. Reject ref/out/in, params, optional, generic scenario methods and
overloaded scenario methods in this initial case-enabled API. Report targeted errors rather than
emitting invalid generated C#. Existing parameterless scenario support is unchanged.

## Generator and analyzer ownership

The analyzer checks names, counts, supported parameter/value forms and implicit type compatibility
against the actual method symbol. Locate diagnostics on the offending case or argument. Add all
new diagnostic descriptors to AnalyzerReleases.Unshipped.md. IDs will use the next available IDs
at implementation time rather than reusing any shipped rule.

The generator uses the same case validation/binding logic so generation cannot disagree with the
analyzer. Suppress definitions for an invalid case-enabled method, with the targeted diagnostic
preventing a partial authorization matrix from compiling successfully.

For each valid case, bind each scenario IParameterSymbol to a typed constant expression before
lowering step arguments. Reuse the existing graph lowering and static execution delegates.
The authored method remains a declaration and is never invoked directly. Parameters can appear
where existing step-argument lowering permits expressions, including title arguments and
condition-step calls. Existing RAUN007 behavior remains for unbound scenario parameters.

A source method produces one independently generated definition per case. Keep the original
method name and MethodInfo as source metadata. Use distinct generated builder names and registry
keys; never register multiple case definitions under the same method-only dictionary key.
Case-enabled overload rejection keeps MethodInfo lookup unambiguous in this version.

## Identity, ordering and execution

Derive each case ScenarioId from the existing method identity plus an unambiguously separated
explicit case key. Derive its step IDs through the current per-scenario mechanism. Never include
row ordinal, display formatting, current-culture formatting or serialized arbitrary values in IDs.
Renaming a case deliberately changes its identity; changing its inputs or reordering attributes
does not. Parameterless scenario and step IDs remain byte-for-byte compatible.

Display case scenarios as `endpoint authorization [reader]`. Preserve existing scenario-method
ordering and retain case attributes' source order within each method. Keep the current five-part
MTP tree path; the case name is part of the scenario segment, not a new hierarchy segment.

Each case is an ordinary ScenarioDefinition. Existing setup/world creation, DI scope, cancellation,
resource contention, scheduler state and teardown run independently for every case. The existing
MTP UID filtering and predecessor closure select only the chosen case and its required steps.
The report accumulates each case separately using its unique ScenarioId.

There is no runtime loop over cases inside a scenario. There are no factories, DI argument
injection, member-data providers, cartesian products or discovery-time application startup.
Existing Given steps may construct authenticated clients from an actor enum during execution.

## Acceptance evidence

- Attribute API supports multiple constant/enum rows on one authored scenario.
- Analyzer tests cover arity, implicit conversions and incompatible enums, unsupported inputs,
  missing cases, invalid placement, duplicate/blank keys and unsupported signatures.
- Generator snapshots and compilation tests prove typed substitution, distinct registrations,
  correct source metadata, stable IDs after reordering/argument edits and unchanged legacy output.
- Cases with null, escaped strings, enum flags and numeric boundary values compile and execute
  correctly where their method parameter types accept them.
- Integration tests prove independent case worlds, setup/teardown and failure reporting, plus
  single-case/step filtering without executing sibling cases.
- A small authorization example demonstrates an anonymous and an authorized endpoint request
  with separate expected results, using the repository's existing sample conventions.
- Release build has zero warnings; relevant generator/runtime/MTP checks pass before completion.

## Alternatives considered

Generated per-method attributes could provide native constructor type checking and completion,
but the user explicitly selected one standard attribute with analyzer validation for now. They
also introduce generated-type discovery, naming and accessibility constraints.

Runtime or DI argument factories can construct richer inputs, but cannot reliably define the case
inventory before discovery and filtering. They also add provider failure, lifetime and repeatability
contracts. Deferred until there is a concrete need beyond constants.

Typed member-data providers scale to larger matrices and complex values, but require a separate
discovery/provider contract. They are outside this first version.

Expanding cases as steps inside one scenario would share a world and reporting identity. That
would undermine independent authorization failures and reruns, so every case is a scenario.
