# Resource authoring code fixes: separate project brief

Date: 2026-10-18
Status: Written brief for review and later subagent execution. No code fixes implemented or agents dispatched by this documentation task.
Depends on: [Resource effects, access, actions, and links](2026-10-18-resource-effects-and-links-design.md).

## Goal

Let a user write the intended attribute usage first, then create the required action/role declarations and bind parameters through previewable IDE code actions. Help with authoring, not migration from old Raun APIs. The core attribute model and diagnostics must land first so this project does not implement a competing interpretation of effects or links.

## Existing foundation and missing pieces

Raun.Generator currently contains Roslyn source generation and ScenarioAnalyzer and is packaged inside Raun under analyzers/dotnet/roslyn<version>/cs. It has compiler dependencies but no CodeFixProvider implementation or code-fix test project. Raun.Coverage's Workspaces dependencies are unrelated; do not couple authoring fixes to coverage.

Add a separate editor-facing Raun.CodeFixes assembly and focused test project. Keep Workspaces/code-action dependencies out of the compiler generator and runtime dependency graph. The packaging owner must prove discovery by the intended IDE host and prevent compiler-host load failures; do not assume putting any extra DLL in the generator directory is sufficient. No VSIX, new published package, or release is automatically authorized by this brief. Record any additional distribution requirement before implementing it.

The first deliverable is verified Roslyn fixes and appropriate NuGet packaging. Native Visual Studio and ReSharper validation are separate evidence items. Roslyn unit tests are not proof that every IDE loads a provider.

## Authoring workflows

### Create an action from usage

Given an unresolved attribute on a step return, offer explicit alternatives such as Create Booked as a Create action, Create Booked as an Update action, and the other valid CRUD choices. Never infer an effect or exclusive access from an English name such as Booked.

Create a suffix-free action class in the selected source namespace/file, deriving from the selected built-in effect or a selected existing domain action. Respect imports, qualification, accessibility and the project's formatting. Do not create a BookedAttribute plus a conflicting Booked peer. Do not modify generated source. Existing symbols and namespace/type collisions require an explicit alternate destination or a clear diagnostic, not an overwrite.

### Create a nested role from usage

For an unresolved Booked.Patient or Booked.Slot annotation, locate the owner action and offer to create its nested PatientAttribute/SlotAttribute as ResourceLinkAttribute<TResource,TAction>.

Infer the canonical resource expectation only when metadata gives one unambiguous answer. A mapped DTO/row must resolve through existing identity mappings, not be assigned to a new resource kind. A key-only string must have ResourceKey<T> metadata or an explicit user selection; do not infer a resource kind from its name. If the action is missing, let the user select/create it through the owner workflow.

The target action is an explicit choice: for example create Slot using Reserved, or using Read/Update/Create/Delete. Known candidates can be offered as named choices, but do not silently choose a mutation effect. If the declaration lives in metadata or generated/noneditable source, offer the raw Link form or a user-owned declaration instead of trying to edit a binary. Existing source classes spread across partial files need one deterministic editable insertion point.

### Bind an existing owner's roles

Adding a return Booked action to a step should offer Bind Booked relationships to parameters. Use the owner's declared schema; do not invent missing roles from parameter names.

For each required role:

| Candidates | Behavior |
|---|---|
| One compatible parameter | Bind it automatically in the proposed edit. |
| Multiple compatible parameters | User chooses. Never break a tie by parameter name or source order. |
| No compatible parameter | Offer to add the missing parameter, type and role marker to the signature. |
| Role already correctly bound | Preserve the binding. |

Compatibility is canonical identity, including IResourceAlternate mappings and ResourceKey<T> annotations. An unannotated string is not a match. Existing supported resource collections use their element identity; unrelated collections do not qualify.

The code action is explicit and previewable; adding return Booked does not silently edit the document while the user types. Bind all unambiguous roles in one proposed edit. Present ambiguous roles as named parameter choices, possibly incremental actions, so the user can choose without a custom modal wizard or a combinatorial list of whole-method variants.

### Add a parameter when no candidate exists

Use the role's declared target type as the normal default if it is a valid value representation. Choose a collision-free, idiomatic parameter name from the role and attach the nested marker. For an identity-only owner that has no usable self representation, offer a typed key parameter (ResourceKey<TResource> metadata on string) or an explicitly selected mapped representation; do not manufacture an instance of an identity marker.

Insert required parameters before optional parameters and params arrays; retain cancellation-token conventions where possible without changing existing argument bindings. Inspect all roles in a batch to avoid accidentally binding two unrelated roles to one newly created parameter or adding duplicate parameters. Existing explicit bindings stay intact.

A new parameter changes the signature. Show the signature change in preview and surface affected call sites/references. Do not insert fabricated patient values, null!, default arguments, new setup steps, or claimed calls to update every caller. Missing caller arguments remain ordinary actionable compiler errors until the user supplies domain data. Generated declaration tests should compile; a signature-change fixture may intentionally report only the expected missing-argument errors at untouched callers.

An existing parameter with an explicit raw effect/link combination is not overwritten. Offer conversion to a packaged marker only when it preserves the selected action, canonical expectation and role; otherwise retain the explicit composition or require a user choice. Never add a packaged marker that creates competing effect declarations.

## Diagnostics and shared contracts

The analyzer owns diagnostics; the code-fix project owns edits. Keep semantic checks in the core normalization layer so the IDE does not reimplement identity or action validation differently. Prefer structured diagnostic properties for owner, role, expected identity and source location over parsing message text. Use compiler-available identifiers such as metadata/documentation IDs; do not pull editor SymbolKey/Workspaces dependencies into the generator merely to encode properties.

For unresolved attribute usages, providers may need to respond to appropriate compiler missing-type/member diagnostics in addition to Raun diagnostics. Restrict those actions to supported attribute contexts, validate the proposed edit semantically, and coexist with the IDE's ordinary Create type actions. Recompute candidates from the current document when applying a fix; do not apply stale choices after the user edits the method.

No fixes for legacy Load/Edited/Reference/Consume APIs. No migration project. No automatic change from shared to exclusive access to silence a conflict. No guessed scenario reservations, business operation bodies, or external service configuration.

## Proposed worker ownership for the later project

Use separate workers only after the core model and diagnostic/property contract are stable. This is a proposed dispatch plan, not permission to start agents in this documentation pass.

1. Packaging and integration owner: owns src/Raun.CodeFixes/*.csproj, test/Raun.CodeFixes.Test/*.csproj, solution/central package changes, Raun NuGet packaging, and shared code-action test infrastructure. Establishes provider discovery and dependency isolation first. Other workers do not edit these files.
2. Declaration-scaffolding worker: owns action/role creation providers and their dedicated tests. Consumes the shared diagnostic contract and test infrastructure. Does not alter the core analyzer contract independently.
3. Binding/signature worker: owns role binding, ambiguity choices, missing-parameter edits and their dedicated tests. Consumes canonical identity/schema helpers and the same action contract. Does not edit scaffolding providers.
4. Final reviewer/integrator: reviews the combined branch, safety of inferred edits, packaging evidence, and examples. Integrator owns any shared-contract correction and coordinates dependent worker adjustments.

Every worker must be told it is not alone in the codebase, must preserve other workers' changes, and must stay within its ownership. Scaffolding and binding can run in parallel after the shared foundation is stable. Do not split source/runtime/generator semantics across competing implementations in this follow-up.

## Acceptance and meaningful tests

- [ ] Code action creates the requested action with each chosen CRUD effect; no unchosen mutation semantics.
- [ ] Code action creates a usable nested role with a canonical target and explicit target action.
- [ ] Generated declarations compile, produce the intended metadata and pass Raun's analyzers.
- [ ] Unique mapped DTO/row/key candidates bind correctly; opaque unannotated strings do not.
- [ ] Two Slot candidates produce explicit choices and no silent guess.
- [ ] Zero candidates offer a correctly typed/annotated parameter; optional/params signatures, naming collisions and identity-only targets are handled.
- [ ] Existing bindings, action labels, imports, comments and caller values are preserved.
- [ ] Fixes are idempotent and refuse stale/conflicting edits or uneditable declarations.
- [ ] Fix All is offered only for deterministic operations: unique bindings and equivalent declaration creation without duplicate types. Ambiguous bindings and signature changes must not get an unsafe blanket Fix All.
- [ ] Preview and changed-document assertions are supplemented by compilation and analyzer assertions; tests must not merely snapshot the provider's preferred text.
- [ ] Packaging proves editor dependencies do not become runtime dependencies or compiler-generator load failures.
- [ ] Verify native IDE behavior where available and state untested host behavior explicitly.
- [ ] Run the repository's required checks and one independent final review before integrating. No release unless separately requested.

## Alternatives rejected

- Build migration fixes first: there are no external users requiring them; direct repository changes are sufficient.
- Guess effect from Booked/Reserved names: English naming is not an access contract.
- Guess between equal canonical candidates by parameter name: user explicitly requires choosing.
- Report no matching parameter and stop: user explicitly wants parameter creation offered.
- Generate caller values or defaults to hide signature errors: the IDE cannot invent test intent.
- Generate peer containers for suffix-named attributes: conflicts with the chosen explicit nested-type design.
- Add IDE Workspaces dependencies to Raun.Generator: compiler and editor hosts have different responsibilities and loading constraints.
