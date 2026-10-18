# Resource CRUD and role links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Core execution is inline in the parent workspace; the separate Astra worktree owns code fixes. Steps use checkbox syntax for tracking.

**Goal:** Implement the approved CRUD/effect/access/link model and migrate repository consumers without compatibility shims.

**Architecture:** Normalize four effects separately from immutable role-named links. Runtime reflection and compiler metadata readers expose matching schemas; nested markers package an action and a link, while explicit Link<TOwner> declarations use the same binding pipeline. Scenario admission remains independent.

**Tech Stack:** .NET 10 runtime, netstandard2.0 Roslyn generator/analyzer, xUnit v3, existing self-contained report and Playwright harnesses.

**Spec:** ../specs/2026-10-18-resource-effects-and-links-design.md

## Global Constraints

- Preserve canonical identity/alternate/key resolution, labels and frozen snapshots.
- Four effects only: Create, Read, Update, Delete. Read is Shared; other effects are Exclusive.
- Retain scenario Shared/Exclusive/Pooled admission through setup, teardown and disposal.
- No legacy aliases, migration fixes, generated peer containers, tags or releases.
- Preserve discovery/filter identity and authored titles; MTP must remain usable.
- Do not edit code-fix-owned project/packaging files without coordinating with Astra.
- Use tests first, zero-warning builds and one final independent review.

## Review Focus

- Same canonical target appears under two roles: retain both links, emit one selected action per annotation, never invent extra shared effects.
- A failed return yields no source link; key-only targets keep names without borrowed data.
- Multiple owner applications require explicit Source selection; nested owner/role inheritance cannot silently select an owner.
- Mapped external rows, identity-only owners and collection/key inputs normalize identically in reflection, generation and the editor facade.
- Exclusive scenario reservations survive cancellation, teardown and async disposal, independently of Read-only step effects.

## File structure and settled names

Existing observation record Raun.Model.ResourceEffect becomes ResourceObservation, with Effect:Raun.ResourceEffect; enum Raun.ResourceEffect replaces LifecycleVerb and ResourceActionKind. ResourceLink replaces the old lineage record with Source/Role/Target identities. ResourceAction retains Effect/Name/AttributeType/Relationships; relationship descriptors expose OwnerType/MarkerType/Name/ResourceType/ActionType. ResourceContext exposes CRUD/Apply and Link recording separately. Nested ResourceLinkAttribute<TResource,TAction> is a distinct Attribute family; LinkAttribute<TOwner>(string role) has optional Source.

Public compiler-only facade for Astra: Raun.Generator.ResourceCodeModel.TryGetParameter(Compilation,IParameterSymbol,out ResourceParameterInfo?) and TryGetAction(Compilation,INamedTypeSymbol,out ResourceActionInfo?). ParameterInfo: ValueType, CanonicalType, IsKey, IsCollection, IsIdentityOnly. ActionInfo: AttributeType, Effect string, Roles. RoleInfo: OwnerType, MarkerType, Name, ResourceType, CanonicalType, ActionType. Missing required binding is RAUN036 with properties Raun.OwnerId/Raun.RoleName/Raun.ExpectedResourceId/Raun.MarkerId/Raun.Source; DocumentationCommentId values, not Workspaces SymbolKey.

### Task 1: Runtime contracts, independent links and metadata

Own src/Raun/Resources and src/Raun/Model resource records; resource tests. Keep ContendedResources semantics unchanged.

- [x] Add a behavioral contract regression proving enum names are exactly Create/Read/Update/Delete, Seed is named Create, and Reserve implies Exclusive.
- [x] Observe RED against the old model.
- [x] Add ResourceObservation, CRUD enum/extensions/attributes, nested role schema, raw Link metadata and ResourceLink recording. No attribute constructors/getters run during schema discovery. Link recording adds no claim or effect.
- [x] Runtime tests prove explicit/packaged metadata parity, self-link rejection, canonical mapping/key expectations, role-sensitive dedup, unchanged snapshots, and no link for failed source creation. Retain ledger and reservation tests, adapting their terminology without weakening order assertions.

### Task 2: Compiler role normalization, analyzer and editor facade

Own ResourceDefinitionReader, ResourceRoleMetadata, RelationshipReader, AttributeReader, ScenarioAnalyzer, diagnostics/release notes, and new compiler facade; generator tests.

- [x] Add failing generator/analyzer tests for Booked.Patient/Slot, raw equivalence, key/alternate/collection targets, and Reserve parallel conflicts.
- [x] Replace property/Reference/Consume schema discovery with nested role descriptors and resolve packaged target actions as parameter effects.
- [x] Bind required roles to one parameter each, enforce owner source selection and emit RAUN036 at the owner with structured properties. Diagnose missing, duplicate, malformed, hidden, inaccessible and conflicting declarations at editable locations.
- [x] Expose the compiler facade by reusing the same normalization and canonical discovery; send actual availability and any contract changes to Astra.
- [x] Verify declarations from referenced assemblies and source normalize the same way without executing user code.

### Task 3: Lower separate operations and migrate repository consumers

Own ScenarioParser/ScenarioEmitter/IR resource claims, both samples and generator fixtures/snapshots.

- [x] Add failing execution tests showing Read patient + Reserve/Update slot + Booked/Create result creates exactly those effects and Patient/Slot links.
- [x] Emit parameter actions before calls and return actions/return-owned links only after successful results. Resolve explicit Source without positional guessing. Links invoke the independent recorder, never an effect operation.
- [x] Replace old Load/Edited/Reference/Consume usages in runtime/generator/sample tests with intentional CRUD and role links. Preserve all existing guard/merge/order assertions. Replace tests for retired property APIs with equivalent new schema-invalidity/ownership tests rather than disabling them.
- [x] Use Seed in natural setup examples; use suffix-free Booked/Reserved/etc with nested markers and one explicit raw example. Preserve mapping, optional argument/title and test-framework examples.
- [x] Review changed generator snapshots and accept only the expected CRUD/link code changes.

### Task 4: Reporting, logs, telemetry and current docs

Own report model/builder/template and log/span resource consumers, browser/JS tests, README.

- [x] Add regression evidence proving role names survive equal endpoints and links use actual CRUD/action endpoints without phantom target effects.
- [x] Replace verb-kind lineage rendering with Source/Role/Target. Preserve matrix popovers, columns, labels/snapshots, timing backgrounds, branch groups and diagnostic navigation.
- [x] Remove live old APIs/vocabulary from runnable source and current docs; historical specs remain historical.
- [x] Run report JS/browser gates and regenerate the real appointment report.

### Task 5: Integration and final verification

- [x] Build Raun.slnx with zero warnings; run focused resource/generator suites then dotnet test Raun.slnx.
- [x] Run the existing Node report tests and both browser harnesses with real sample output; run the Aspire sample through its existing test path.
- [x] Search live source for obsolete public APIs and verify code-fix integration seams were not overwritten.
- [x] Obtain one independent whole-change review, fix material findings with regressions, and record exact evidence.
- [x] Commit/push the green core change; keep Astra's separate code-fix branch separate until independently ready. No release.

## Completion evidence

Core implementation: 3537a48. Independent Astra High review and fixes are recorded in
[the final review](../../reviews/2026-10-18-resource-model-review.md). Both Important findings were
reproduced with failing regressions and fixed. Final `dotnet test Raun.slnx`: 1756 total, 1749 passed,
seven expected branch skips, zero failures. Build: zero warnings/errors. 72 Node report tests and
both browser harnesses passed on the regenerated appointment report; the real Aspire sample passed.
Code-fix integration stays on its independently verified branch. No release was created.
