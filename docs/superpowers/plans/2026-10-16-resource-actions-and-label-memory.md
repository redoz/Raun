# Custom Resource Actions and Label Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Support reusable custom resource attributes and reuse useful resource names across ordered steps without changing access rules or recorded evidence.

**Architecture:** Custom attributes inherit one of the five primary resource roles. Immutable action metadata keeps the semantic effect separate from the display name. A scenario-owned label memory supplies per-step views based on the existing scenario graph; reporting consumes recorded action names and labels.

**Tech Stack:** C#, .NET 10, Roslyn incremental generator (netstandard2.0), xUnit v3, self-contained HTML/JavaScript, Playwright report checks.

**Spec:** [Approved actions and label-memory design](../specs/2026-10-16-resource-actions-and-label-memory-design.md).

**Status:** Approved for execution, including the Astra High plan-review corrections. Preserve native execution with one final reviewer. No release.

## Global Constraints

- Keep the seven LifecycleVerb values and existing conflict/dedup precedence.
- Reference and Consume remain lineage relations with explicit endpoints; no standalone relation attributes.
- Recognize exact Raun role bases through inheritance; do not infer semantics from names or run attribute constructors/getters/static initializers.
- Preserve built-in labels and legacy generated/model callers; add no required generated-model properties.
- Preserve input claims before calls and return-dependent claims after calls. Do not re-evaluate arguments or weaken guard/merge assertions.
- Observe labels in explicit scenario-owned state, never the DI identity resolver or a static cache.
- Preserve recorded labels/snapshots; key-only references have no object snapshot. Keep :id as the raw key.
- Use the existing ScenarioGraph ordering, including merge, guard, and ordering-only edges; do not let thread completion choose names.
- Write accurate ASD-STE100-style XML comments for all five attributes and lineage properties, as specified in the approved design.
- Tests first; zero warnings; register new diagnostics in AnalyzerReleases.Unshipped.md.
- Preserve the current resource matrix and effect-table alignment; verify its browser harness.
- Commit/push completed work. No release, tag, or NuGet publication.

## Review Focus

1. A library exposes steps decorated with a private or generic custom attribute: generated/runtime metadata agree without inaccessible C# references or user initialization. Tasks 1 and 3.
2. Custom attributes share a display name or inherit DisplayName through several levels: retain distinct action identities, correct effects, and exact labels. Tasks 1, 2, and 3.
3. Two independent producer branches supply different names before a merge: choose by causal order and then node order, regardless of completion order. Task 4.
4. One shared DI resolver serves concurrent/repeated scenarios with identical keys: names cannot escape their originating scenario. Task 4.
5. A key-only reference occurs in skipped-step output or teardown after a failed producer: reuse only observed names, retain prior snapshots, and never invent data. Task 4.

---

### Task 1: Extensible roles, action metadata, and public comments

**Files:** Modify `src/Raun/Resources/ResourceRoleAttributes.cs`; create `ResourceAction.cs` and `ResourceActionMetadata.cs` in that directory; test `test/Raun.Test/Resources/ResourceActionTests.cs` and update `ResourceRoleAttributeTests.cs` / `SubjectAttributeTests.cs` without dropping assertions.

**Interfaces produced:**
- All five role attributes are nonsealed, with `AttributeUsage(Parameter | ReturnValue | Method)`; keep existing lineage properties and named-argument behavior. Multiple competing roles are rejected by task 3.
- Immutable `ResourceAction`: `LifecycleVerb Effect`, `string Name`, `Type? AttributeType`; `static ResourceAction For<TAttribute>() where TAttribute : Attribute`, `static ResourceAction For(Type attributeType)`.
- Generated-code helper `static ResourceAction FromGenerated(Type scope, string assemblyQualifiedAttributeTypeName)` resolves an exact type in the scope assembly's load context, then uses the same metadata factory. No assembly enumeration or user-code execution. Use context-aware Type.GetType resolution for generic argument assemblies, not a default-context lookup by short name.
- Runtime metadata caches are keyed by actual Type, not label or assembly-name strings. Built-in descriptors retain their existing verb names; custom descriptors retain their concrete attribute Type.

- [x] Write failing `ResourceActionTests`: Approved derives Edited and uses DisplayName("Approve"); default is Approved; indirect inheritance uses nearest explicit DisplayName; blank explicit names fail; an unrelated attribute fails. Assert `action.Effect == Edit`, `action.Name == "Approve"`, and `action.AttributeType == typeof(ApprovedAttribute)`.
- [x] Add failing cases for equal display names on different attribute types/effects; closed generic attributes use their source type name without CLR generic arity; generated metadata resolves private and referenced attribute types in the correct load context (review focus 1/2). A throwing constructor/static initializer must remain unexecuted by both factories.
- [x] Update the attribute-location tests to require the same parameter/return/method locations for all five bases, retaining lineage defaults and subject checks.
- [x] Run `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release -- --filter-class '*ResourceActionTests' --filter-class '*ResourceRoleAttributeTests' --filter-class '*SubjectAttributeTests'`; expect failures for the missing behavior before implementation.
- [x] Implement exact base-type recognition and CustomAttributeData-based DisplayName lookup. Remove a terminal Attribute suffix after removing CLR generic arity; preserve built-in display names. Reject blank labels and unsupported types with actionable ArgumentException messages. If CA1813 flags the role bases, document the intentional attribute extensibility in a targeted suppression; keep other analyzer checks enabled.
- [x] Write the requested XML comments now: one effect meaning per summary; location, shared/exclusive conflict behavior, recording time, and custom-label rules in remarks. Explain that the step code performs the operation. State explicitly that Consumes does not delete the input and References targets retain a separate identity. Define technical terms consistently; do not claim automated STE certification.
- [x] Re-run the focused tests and `dotnet build Raun.slnx -c Release --no-restore`; expect passing tests and zero warnings/errors. Commit `feat(resources): define custom resource actions` with a body explaining one-definition semantics and metadata-only discovery.

### Task 2: Runtime action application and stable aggregation

**Files:** Modify `src/Raun/Resources/ResourceContext.cs`, `src/Raun/Model/ResourceEffect.cs`, `src/Raun/ScenarioContext.cs`; test `test/Raun.Test/Resources/CustomResourceActionTests.cs` and existing `ResourceContextTests.cs` / `ResourceIdentityRoleTests.cs`.

**Interfaces consumed/produced:**
- Consume ResourceAction from task 1; add `ValueTask Apply<T>(ResourceAction action, T resource) where T : notnull` and `ValueTask Apply(ResourceAction action, ResourceIdentity identity)` to ResourceContext.
- Add `ValueTask Create(ResourceIdentity identity)` and `ValueTask Load(ResourceIdentity identity)`; preserve existing overloads and typed source extraction.
- Add optional `ResourceAction? Action` to ResourceEffect; Verb remains the semantic LifecycleVerb. Internal recording receives the descriptor and claims the ledger with its Effect.
- Internal resource-observer payload carries semantic effect plus optional action; success log text uses action.Name, conflict text/telemetry semantics remain based on LifecycleVerb. Preserve chronological snapshots/labels and existing public constructors.

- [x] Write failing tests: custom Edit conflicts exactly like Edit in both claim orders; custom Read remains shared; key-only Apply/Create/Load produce no wrapper payload or snapshot; generic Apply preserves declared base-type key/label selection.
- [x] Pin aggregation: built-in Read/Edit still yield one resource effect with existing precedence; repeated same custom action coalesces with first timestamp/latest non-null payload; different custom attribute Types remain distinct even with the same Name (review focus 2). A built-in effect and custom effect can coexist for one canonical identity.
- [x] Assert every observation remains in the log in call order, with its own action name, label, and frozen snapshot. Refused claims record no accepted effect; existing conflict diagnostics retain semantic verbs.
- [x] Run `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release -- --filter-class '*CustomResourceActionTests' --filter-class '*ResourceContextTests' --filter-class '*ResourceIdentityRoleTests'`; expect intended failures.
- [x] Implement Apply with the same identity authority and claim-before-evidence ordering. Aggregate built-ins by identity and custom actions by (identity, concrete attribute Type); do not key custom actions by display text. Update observer plumbing without weakening existing assertions.
- [x] Re-run the focused tests and zero-warning solution build. Commit `feat(resources): record domain actions with base effect semantics`.

### Task 3: Generator/analyzer support for derived attributes and wider locations

**Files:** Modify `src/Raun.Generator/Lowering/AttributeReader.cs`, `Ir.cs`, `ScenarioParser.cs`, `ParallelAccess.cs`, `src/Raun.Generator/Emit/ScenarioEmitter.cs`, `Analysis/ScenarioAnalyzer.cs`, `Diagnostics/Descriptors.cs`, `AnalyzerReleases.Unshipped.md`; create `src/Raun.Generator/Syntax/AssemblyQualifiedTypeName.cs`; test `test/Raun.Generator.Test/CustomResourceActionTests.cs`, existing resource/lineage/key/incremental/snapshot tests, and GeneratorHarness referenced-library helpers only if needed.

**Interfaces:**
- Add value-equatable lowered action metadata containing base verb, display name, and assembly-qualified concrete attribute type name. ResourceRoleClaim gets optional action metadata; retain its existing built-in shape when absent.
- AttributeReader exposes `ParameterAction(IParameterSymbol)` / `ReturnAction(IMethodSymbol)` with the same method-shorthand override rule as existing roles. Existing role consumers use the action's base verb. Select the effective role attribute once, then derive action and lineage from it. An explicit return attribute replaces the complete method-level role, including empty or different lineage arrays. ProducerLineage recognizes derived bases and inherited named properties.
- AssemblyQualifiedTypeName renders exact CLR type identities, including nested/constructed generic arguments and their assemblies. Custom claims emit `Resources.Apply(ResourceAction.FromGenerated(typeof(RaunGenerated), typeIdentity), value)` using syntax factories; no source parsing or direct references to inaccessible attribute types.
- RAUN027 (Error): invalid custom action metadata, including blank DisplayName. RAUN028 (Error): competing role attributes on one subject. RAUN029 (Error): method/return role has no supported resource return. Add all to release tracking and appropriate SupportedDiagnostics lists.

- [x] Write failing generator tests for all five effects on parameters and returned values, method shorthand/return override, direct/indirect derived attributes, private/nested and referenced-library action types, generic metadata identities, and default/explicit names. Assert generated/runtime descriptors match task 1 (review focus 1/2).
- [x] Assert built-in and custom Create inputs conflict with Read in both declaration orders; Loaded/Read remains shared. Static classification must match all seven runtime effects. Assert custom Edit triggers the existing parallel-conflict analyzer, custom Read stays shared, and Created/Loaded/Edited lineage works on newly valid input locations. Test several resource parameters plus a return, and key-only input roles for all five effects.
- [x] Add diagnostics for blank labels, competing roles, and method roles on Task/ValueTask without a value. Include unadorned local methods without StepName and separately compiled library steps. Validate resolved step symbols at invocation/lowering, report metadata-only errors at the consumer call, and avoid duplicate local declaration/call diagnostics. Test explicit return roles with empty and different lineage overriding method lineage. Same-name attributes outside Raun's inheritance tree must not become resource roles. Attribute discovery must not run constructor bodies or initializers.
- [x] Run `dotnet test --project test/Raun.Generator.Test/Raun.Generator.Test.csproj -c Release -- --filter-class '*CustomResourceActionTests'`; require the intended failures.
- [x] Implement symbol-based hierarchy recognition, action IR, accurate diagnostics, lineage handling, and metadata emission. Keep all pre-call versus post-call claim placement, argument capture, stack-bound invocation, graph dependencies, and return timing unchanged.
- [x] Run the full generator project. Review received snapshots before acceptance; unchanged built-in scenarios should retain their source output. Require caching and emission-discipline tests to pass, plus zero-warning solution build. Commit `feat(generator): lower reusable resource action attributes`.

### Task 4: Scenario-owned label memory across every presentation path

**Files:** Create `src/Raun/Resources/ResourceLabelMemory.cs`; modify `ResourceIdentityResolver.cs`, `ResourceContext.cs`, `src/Raun/Reporting/Messages/MessageFormattingContext.cs`, `StepMessageBuilder.cs`, `src/Raun/ScenarioContext.cs`, `Model/StepNotes.cs`, `Model/ScenarioGraph.cs` only for reusable ordering helpers, `Scheduling/ScenarioScheduler.cs`, `Running/RunLoop.cs`; test `test/Raun.Test/Resources/ResourceLabelMemoryTests.cs`, `Reporting/ResourceLabelMemoryIntegrationTests.cs`, and generator key/title/return tests.

**Interfaces:**
- Stateless resolver helper `internal bool TryResolveSourceLabel(Type sourceType, object instance, out string label)` returns true only for nonblank selector/alternate/self label evidence. Existing ResolveLabel wrappers remain compatible.
- `ResourceLabelMemory(IReadOnlyList<ScenarioNode> nodes)` owns canonical-identity observations. It returns immutable per-step `ResourceLabelScope` views with `ForStep(int nodeIndex)`; a standalone view is used by existing public formatting/context constructors.
- Scope `string Resolve(ResourceIdentityResolver resolver, ResourceIdentity identity, Type? sourceType, object? value)` handles meaningful observation, eligible memory, and display fallback. It never serializes or retains value.
- MessageFormattingContext internal `ForStep(int)` and `WithFormatProvider(IFormatProvider?)` share the same memory; public constructors remain unchanged. A scheduler-only construction path gives ScenarioContext and ResourceContext the exact step formatting scope, including invariant formatting and teardown token clones.
- One memory per scenario execution, including each preflight-skipped scenario. No new required public model members or resolver-wide observed-label cache.

- [x] Write failing memory tests for good label then key, updated label, blank/fallback preservation, same key/different kinds, canonical alternates, failed label selectors, and unmodified :id/key-only :data snapshots. A source-provided label equal to the fallback text is still meaningful; do not compare text to infer quality.
- [x] Write tests for own-step sequence, causally newer producer preference, unrelated-step invisibility, and independent predecessor branches with different names joined by a reader. Reverse completion order and require identical names (review focus 3). Include manual graphs whose node indices are not topological.
- [x] Write execution tests with concurrent and repeated scenarios sharing one DI resolver and identical identities; assert names remain scenario-local (review focus 4).
- [x] Cover setup, argument preparation, initial and completed titles, notes, structured/interpolated logs, imperative and generated effects, skipped-step formatting after observed failure, teardown, and cleanup token views (review focus 5). Assert earlier captured labels and derived runtime snapshots remain unchanged.
- [x] Run `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release -- --filter-class '*ResourceLabelMemoryTests' --filter-class '*ResourceLabelMemoryIntegrationTests'`; require intended failures before implementation.
- [x] Implement a synchronized observation store retaining only identity, label, producer node, and within-step sequence. Filter to the reader's own prior observations and graph predecessors; discard causally dominated candidates, then tie-break maximal independent producers by node order. Do not select by wall-clock completion. The final teardown scope can see all drained scenario observations because teardown is the execution's implicit join.
- [x] Fix StepNotes.FormatTitle and FormatCompletedTitle to use WithFormatProvider instead of reconstructing contexts. Prove normal initial titles consume predecessor names and completed-title observations reach later key-only references.
- [x] Add a cancellation cutoff regression with barriers: move a draining predecessor observation before/after the pending reader becomes cancelled, preserve final observations, and require identical frozen labels. Request cancellation promptly, but defer cancelled pending-node presentation until relevant predecessors drain. Preserve watchdog ownership and never publish a temporary title and rewrite it later.
- [x] Bind a memory once in the scheduler and use a per-node view at every formatting call. Preserve scope in invariant formatting and cleanup clones. Use the same label resolution in effects and message references. Make key-only lookups use memory without invoking object label functions or copying old payloads.
- [x] Re-run focused tests and full runtime/generator projects sequentially; expect all passing, existing snapshot/graph/timeout ownership assertions intact, and zero-warning build. Commit `feat(resources): remember names across ordered scenario steps`.

### Task 5: Display domain actions in the existing report and README

**Files:** Modify `src/Raun/Reporting/Html/HtmlReportModel.cs`, `HtmlReportModelBuilder.cs`, `report-template.html`, `test/Raun.Test/Reporting/HtmlReportModelBuilderTests.cs`, `message-view.test.cjs`, `test/Browser/resource-matrix.cjs`, `README.md`; update the canonical sample with a small real domain action and key-only follow-up if needed to exercise the browser fixture.

**Interfaces:** ReportEffect and ReportResourceEvent retain semantic `Verb` and gain optional `ActionName`. Render visible text with `ActionName ?? Verb`; retain semantic styling/access logic and canonical ResourceId. Use recorded labels and data only; do not resolve names again while building a report.

- [x] Add failing C#/JavaScript/browser assertions for an Approve custom action, a default-named custom action, legacy data without ActionName, duplicate display names, labels containing HTML characters, and key-only follow-up showing a predecessor's good name with no snapshot.
- [x] Run the focused report tests before implementation; require intended failures. Preserve every resource-matrix row/order, selection/grouping rule, detail/log alignment, snapshot/failure display, and mobile-access assertion.
- [x] Implement optional action-name serialization and visible labels in matrix cells, effect tables, event history, hover/detail content, and structured resource logs. Keep base verbs for semantic CSS/classes and access filters. Include custom names wherever a legend/filter lists actions; do not hide them behind a fixed known-verb list.
- [x] Update README with default and DisplayName custom attributes, inherited semantics, widened attribute locations, imperative Apply, and automatic cross-step labels. Explain predecessor visibility, stable parallel tie-breaks, scenario isolation, and no retroactive evidence changes. Review every role/property XML comment against the spec's STE requirements.
- [x] Run `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release -- --filter-class '*HtmlReportModelBuilderTests'`, then `node --test test/Raun.Test/Reporting/message-view.test.cjs test/Raun.Test/Reporting/coverage-view.test.cjs`. Generate the canonical sample report and run `node test/Browser/resource-matrix.cjs <absolute-report-path> artifacts/resource-actions/browser`; require all checks passing.
- [x] Commit `feat(report): show domain actions and remembered resource names` with a body explaining preserved semantics/evidence and updated documentation.

### Task 6: Full verification, one final review, and push

**Files:** Update this plan and the approved spec with implementation/verification status. Keep temporary logs under this plan's ignored workspace; do not alter dependency versions or release configuration.

- [x] Run `dotnet restore Raun.slnx --locked-mode`; require success and no unplanned lock-file changes.
- [x] Run `dotnet build Raun.slnx -c Release --no-restore`; require zero warnings/errors. Run it separately from tests that launch timeout-host fixtures to avoid file-lock copy warnings.
- [x] Run `dotnet test Raun.slnx -c Release --no-build`; require zero failures and inspect sample results. Do not run overlapping full suites.
- [x] Run report JavaScript checks and the resource-matrix browser harness on the generated sample report; require passing results and inspect the custom-action/key-only fixture.
- [x] Pack Raun, Raun.Mtp, Raun.Coverage, and Raun.Aspire in Release with --no-build/--no-restore to a task artifact directory; require four packages. Do not publish.
- [x] Dispatch one final fresh staff reviewer over the complete feature diff, spec, plan Review Focus, and execution rulings. Ask about semantic/display separation, private/generic metadata identity, input/return timing, graph-based label visibility, snapshot retention, source-specific labels, and generated/model compatibility. Fix substantive findings with RED-to-GREEN regressions and rerun affected/full checks as required.
- [x] Record the final evidence and review outcome, complete the checklist, run `git -c core.whitespace=cr-at-eol diff --check`, commit corrections/docs with why bodies, and non-force push. Import Git refs into the colocated JJ repository without changing user files. Preserve the managed worktree and report previews. No release.

## Self-review

Each approved requirement has an owner: action metadata and STE comments (task 1), runtime claims and distinct action records (task 2), generation/analyzers/locations (task 3), scenario-scoped names and all formatting paths (task 4), matrix/report/docs (task 5), full validation/review/push (task 6). Interfaces carry concrete attribute Type identity and canonical resource identity separately. The generated metadata helper avoids private-type references and preserves generic argument assemblies/load context. The label-memory path keeps the DI resolver stateless, including invariant formatting and cleanup clones. All five review-focus cases have named behavioral tests. No new startup convention, registry, release, or speculative base effects are introduced.

## Astra High plan review corrections

All five findings are incorporated: deterministic cancelled-step capture after predecessor drain;
call-site validation for unadorned/referenced steps; StepNotes scope preservation; Create input
exclusivity tests; and selection of action plus lineage from one effective role declaration.
The user authorized updating these points and proceeding directly to implementation.

## Completion evidence — 2026-10-16

Implemented and verified in the managed worktree. All five Astra High plan findings were incorporated
before implementation. The final fresh Astra High staff review found two metadata defects: a strong
cache retained collectible load contexts, and a suffix-only default name was accepted only at runtime.
Both regressions failed before their fixes and pass with a weak-key cache and final-name validation.
The reviewer found no further correctness issues, no deferred minor findings, and no behaviors outside
its judgment.

- Locked solution restore succeeded; dependency lock files did not change.
- Release solution build: zero warnings, zero errors.
- Full solution tests: 1,526 total, 1,521 passed, five expected branch skips, zero failures.
- Report JavaScript: 72 passed. Resource-matrix browser checks passed, including real generated
  custom actions, key-only remembered labels without data, duplicate names, HTML characters,
  keyboard inspection, grouping, failure evidence, and mobile access. Screenshot inspected.
- Four local packages built: Raun, Raun.Mtp, Raun.Coverage, and Raun.Aspire. None published.
- The new canonical sample adds five nodes; the MTP total assertion is now exactly 100, with the
  existing exactly-six-node filtered selection assertion retained.
- Execution ruling: equivalent Windows-native bookkeeping replaced POSIX ledger scripts; this
  changes bookkeeping only, and records can be reconstructed from Git if needed.

No product design deviations, release, tag, or NuGet publication.
