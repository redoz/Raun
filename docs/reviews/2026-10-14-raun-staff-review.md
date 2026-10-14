# Raun staff engineering and product review

Date: 2026-10-14
Reviewed commit: e814ece (beta.13 implementation plus README update)
Scope: whole system, read-only. Reviewer inspected README, scenario compiler and diagnostics, runtime graph and scheduler, worlds and DI, resource tracking and contention, teardown, reporting, MTP bootstrap, coverage, samples, designs, and relevant tests. Ran an in-memory generated-scenario probe and a focused DI disposal probe; did not rerun the whole suite.

## Verdict and product opinion

Raun has a coherent, useful product and an architecture worth keeping, but several execution contracts are weaker than the product depends on. Continue investing, pause feature expansion temporarily, and harden evidence accuracy and failure handling before treating it as a dependable foundation for other teams.

Raun makes integration tests explain their execution. Typed scenario composition, dependency-aware execution, individual step results, resource identity and lineage, and portable reports reinforce each other. A failure can be traced through setup, inputs, captured values, application logs, and cleanup.

The best users are teams testing stateful workflows across services, databases, queues, and managed environments. Ordinary unit tests and short isolated API assertions receive less explanatory value for the compiler, phases, worlds, and resource vocabulary they must adopt.

The main adoption risk is familiar async C# syntax with compiler-defined semantics. Scenario methods are never executed directly; supported constructs are lowered, certain familiar refactorings are prohibited, and values move into generated code. This is a language contract that must be explained and preserved.

## Findings

### 1. Argument expressions independently evaluated for title, claim, and call — blocking

Evidence: src/Raun.Generator/Lowering/ScenarioParser.cs:1814 and :1927; src/Raun.Generator/Lowering/DisplayNameBuilder.cs:93.

The compiler copies argument expressions into invocation, resource claim, and title expressions instead of materializing one value. A deterministic generated-scenario probe called `await Given.Use(ProbeState.Next());`, where Next increments a counter and Use accepts `[Read, ResourceKey<Patient>] string key` with `[StepName("Given {key}")]`. It compiled without diagnostics and produced:

```text
TITLE=Given Patient:1
CLAIM=Patient:2
ACTUAL=3
EVALUATIONS=3
STATUS=Passed
```

Why it matters: the report can name one resource, conflict detection can claim another, and the operation can touch a third. GUIDs, factories, clocks, and constructed resource objects make this practical. The step-arguments design promises evaluation once when the consuming step starts.

Where it leads: adding explanatory metadata can change execution behavior. Observation must describe execution without causing additional execution.

Suggested fix: materialize each supplied argument once for a launched node, preserving C# argument order and conversions; share values between claims, invocation, and runtime title formatting. Discovery retains symbolic metadata; skipped-step formatting must not execute factories. Add a combined behavioral regression test.

### 2. Timed-out bodies can outlive teardown and scenario admission — blocking

Evidence: src/Raun/Scheduling/ScenarioScheduler.cs:383 and :943; src/Raun/Running/RunLoop.cs:291.

Timeouts can abandon an operation ignoring cancellation. Teardown runs and the scheduler returns, then the run loop releases contention ownership and disposes the scope while the operation may still run. ScenarioTimeoutTests.A_scenario_timeout_fails_the_running_step_without_waiting_for_it covers abandonment but not the surviving operation lifecycle.

Why it matters: surviving bodies can use disposed services, create resources after cleanup was snapshotted, or mutate infrastructure allocated to another scenario.

Where it leads: an exclusive database can be released while the timed-out body still writes alongside the next scenario.

Suggested fix: explicitly define noncooperative execution policy, including cancellation and bounded cooperative draining. Do not release ownership as though execution stopped if work remains alive. Host abort or process isolation may be needed; arbitrary .NET tasks cannot safely be killed in place.

### 3. Teardown can hang indefinitely after scenario timeout — blocking

Evidence: src/Raun/Scheduling/ScenarioScheduler.cs:414, :504, :531; docs/superpowers/specs/2026-09-04-scenario-teardown-design.md:196.

The scenario timer stops before teardown; callbacks receive CancellationToken.None and have no independent deadline. A hanging callback prevents subsequent cleanup and completion, contrary to the bounded-cleanup design requirement.

Why it matters: cleanup often talks to the same broken environment that caused failure. Catching exceptions does not handle hangs.

Where it leads: a suite with scenario timeouts can still wedge indefinitely during recovery.

Suggested fix: independent bounded teardown policy and callback-specific reporting, consistent with surviving-work ownership policy.

### 4. Scenario DI scopes reject async-only scoped services — blocking

Evidence: src/Raun/Running/RunLoop.cs:383 and :407.

RunLoop creates an ordinary scope and calls synchronous Dispose. A focused probe with the shipped DI assemblies confirmed that an async-only scoped service throws: `'ReviewAsyncOnly' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.`

Why it matters: asynchronous clients and fixtures are natural integration dependencies; passing steps can be followed by a framework failure outside step reporting.

Where it leads: adopting an async scoped service breaks an otherwise working suite.

Suggested fix: asynchronous scope ownership/disposal after teardown, with success, failure, and cancellation behavioral tests.

### 5. Resource key dispatch misses valid explicit interface implementations — worth fixing now

Evidence: src/Raun/Resources/ResourceIdentityResolver.cs:155; label interface dispatch at :59–63.

Resolver detects IResource<T> but searches for public static KeyFor on the concrete type. An explicit static interface implementation can fall through to another identity mechanism, ultimately ToString. Established by inspection, not a separate executable reproduction.

Why it matters: public identity contract and runtime authority disagree; object and typed-key forms can resolve differently.

Where it leads: legitimate implementations of the same interface create different conflict/report identities.

Suggested fix: dispatch KeyFor through the interface, following labels.

### 6. Optional coverage has mandatory compiler/MSBuild dependencies — consider

Evidence: src/Raun.Mtp/Raun.Mtp.csproj:24; src/Raun.Coverage/Raun.Coverage.csproj:13.

Every MTP consumer depends on coverage and its Roslyn workspaces, MSBuild support, locator, and build-host assets even when unused.

Why it matters: classified coverage adds a second environment/compiler integration to the default framework distribution.

Where it leads: collector and SDK workspace compatibility increase maintenance for all consumers.

Suggested move: consider coverage as an explicitly installed integration. This is a packaging judgment, not a demonstrated execution defect.

## Strengths and engineering direction

Preserve the compiler → explicit graph → runtime → report architecture. Runtime owns execution, MTP translates platform concerns, and reports consume events.

- ScenarioGraph.Predecessors centralizes ordering for selection and conflict analysis.
- One authoritative parser replaced competing scenario walkers.
- Contention acquires the whole use set, avoiding hold-and-wait deadlocks.
- Report sink failures are isolated from test results.
- Generator tests compile and execute emitted code, beyond snapshots.

Do not add interfaces or split large files indiscriminately. Repair cross-boundary invariants: one argument-value authority and one authority for execution/ownership termination. Feature tests are substantial, but damaging cases occur at intersections; combined-contract tests are highest value.

Scalability is unproven, not an immediate defect: LINQ expansion creates individual nodes, resource reachability is quadratic, scheduling scans pending nodes, and snapshots serialize complete object graphs. Measure realistic graphs and payloads and establish the intended envelope before optimizing or promising scale.

Priorities: fix argument and lifecycle guarantees; have an outside team build a meaningful integration suite; document supported scenario semantics; separate optional breadth where warranted; benchmark realistic larger suites. The repository establishes capabilities, not external adoption value or shorter diagnosis time.

## Feature opportunities (follow-up)

These are opportunities, not additional correctness defects. They should follow hardening.

### Independently discovered scenario cases — strongest substantial feature

Job: vary roles, outcomes, payment methods, configuration, or boundary values while preserving case-level selection, isolation, and outcomes.

Today one method has one scenario definition/identity; scenario parameters cannot provide inputs. LINQ expands steps within one scenario. Workarounds duplicate orchestration or hide a loop inside a step, losing case reporting/isolation.

Smallest useful feature: statically declared typed cases with explicit names, each with its own scenario identity, Setup, world, and teardown. Begin with constants discoverable without executing user code; defer arbitrary runtime providers.

Value high, semantic cost significant: identity, names, substitution, filtering, diagnostics. First substantial feature after hardening.

Evidence: src/Raun.Generator/Lowering/ArgumentLowering.cs:72; GenStableId.cs:12; ScenarioParser.cs:1352.

### Tags/user metadata and filtering — small and broadly useful

Job: select smoke/nightly/expensive/subsystem suites independent of source naming. Current filter properties expose Phase and Scenario; workarounds filter names, classes, and namespaces.

Smallest addition: repeatable scenario metadata flowing through discovery, selection, and reports; optional class scope with explicit precedence. Reuse MTP filtering. Avoid a built-in taxonomy before user demand.

High value, low semantic cost; near-term.

Evidence: src/Raun.Mtp/Filtering/ScenarioNodePath.cs:80–87; src/Raun/Attributes/ScenarioAttribute.cs.

### Per-type snapshot projection/suppression — strong product fit

Job: select useful captured data while omitting huge collections or unwanted fields. Serialization currently uses the runtime object. Workarounds are wrappers, key-only references, attachments, or serializer annotations.

Smallest addition: per-type projection or explicit suppression, consistently applied to titles, logs, and effects; preserve independent identity/labels. This does not sanitize arbitrary log strings or attachments.

High value, moderate cost due to multiple capture paths. Near-term if real reports are unwieldy; otherwise validate externally first.

Evidence: src/Raun/Resources/ResourcePresentation.cs:38; src/Raun/Reporting/Messages/StepMessageBuilder.cs:39; src/Raun/Resources/ResourceContext.cs:187.

### Canonical identity across object representations — narrow and evidence-driven

Job: PatientDto, PatientRow, and Patient share identity. ResourceKey<Patient> already unifies key parameters with Patient, but Identify<T> retains typeof(T). Workarounds use canonical keys, explicit ResourceIdentity, or wrappers.

Smallest addition: explicit source-representation mapping to canonical resource type/key; retain source snapshot/label, canonicalize claims and lineage. Avoid automatic inheritance/inference.

Potential value high, cost moderate: representation and identity types must be distinct throughout analyzer/runtime/presentation. Add when a concrete multi-representation suite demands it.

Evidence: src/Raun/Resources/ResourceIdentityResolver.cs:69 and :78.

### Expose existing step concurrency limit — cheapest useful addition

Job: limit simultaneous requests inside a wide scenario. Scheduler already supports maxParallelism; normal RunLoop/MTP host does not pass a configurable limit. Scenario concurrency does not solve within-scenario fan-out.

Smallest addition: host and CLI wiring, explicitly per-scenario. Preserve static conflict rules even when execution is serialized.

Low cost, useful operational flexibility; near-term.

Evidence: src/Raun/Scheduling/ScenarioScheduler.cs:20; src/Raun/Running/RunLoop.cs:499.

### Defer and document

Defer built-in retries, assertion framework, broader C# control flow, and elaborate report plugins. Existing steps, assertions, worlds, phases, services, text attachments, runtime roles, and filters provide flexibility. Binary attachments need concrete user demand.

A task-oriented "Can Raun do this?" index would improve discoverability of existing independent branches, dependency selection, capacity, key-only resources, cleanup, logs, and optional titles.

Preferred sequence: fix argument/lifecycle contracts; expose concurrency and metadata; static cases; snapshot projections and canonical mapping under external suite pressure.

## Tracking

All findings above describe e814ece. Later fixes must be verified separately; this document is historical review evidence, not a claim about subsequent versions.

## Remediation checklist

Work in finding order, one item at a time. Findings 2 and 3 need a shared lifecycle policy; record that policy while tackling 2, then implement and verify 3 against it. Keep each finding open until its acceptance checks are complete. Feature opportunities above are outside this checklist.

- [x] **1. Evaluate arguments once.** Fixed in beta.14 and beta.15; regression coverage and reviewer clearance recorded below.
- [x] **2. Own timed-out work until execution actually ends.** Fixed in beta.20; resolution evidence below. Record the noncooperative-work policy and alternatives in a design spec. Verify cooperative cancellation, cancellation-ignoring bodies, and late resource creation. Prove that scopes and exclusive resource ownership cannot be released while surviving work can still use them; prove the chosen policy terminates or reports surviving work explicitly. Do not equate a timeout result with a stopped task.
- [x] **3. Bound teardown independently.** Fixed in beta.21; resolution evidence below. Use the lifecycle policy from 2. Define cleanup deadlines, cancellation, and what happens to callbacks that ignore cancellation. Verify hanging and throwing callbacks, subsequent cleanup behavior, callback-specific reporting, and bounded run completion. Preserve ownership guarantees when cleanup itself survives its deadline.
- [ ] **4. Dispose scenario scopes asynchronously.** Next item. Verify async-only scoped services on successful, failed, and cancelled scenarios. Prove disposal occurs once, after teardown and after any surviving work is handled under the agreed policy. Define and test how disposal failures are reported.
- [ ] **5. Dispatch resource keys through the interface contract.** First reproduce an explicit static `IResource<T>.KeyFor` implementation. Verify interface dispatch and matching object/typed-key identities in claims, lineage, and reports; preserve existing public implementations and fallback identity behavior.
- [ ] **6. Decide whether coverage should be an optional integration.** This is a packaging decision, not a confirmed runtime defect. Inspect the consumer dependency graph and record alternatives, compatibility costs, and the decision. If separated, verify a consumer without coverage avoids its compiler/MSBuild dependencies and an opted-in consumer retains coverage behavior. If retained, document the rationale and mark this item resolved by decision.

For each item before marking it complete:

- [ ] Add behavioral regression evidence for the original failure (or dependency/consumer evidence for 6).
- [ ] Implement the smallest complete fix; document any changed public behavior or configuration.
- [ ] Run relevant tests, report interaction checks when affected, and a zero-warning solution build plus the full solution tests before release.
- [ ] Review the final change, including interactions with earlier lifecycle fixes; record any remaining limitations.
- [ ] Record the resolution, verification evidence, commit, and release in this document; then check off the finding.

The per-item checks are a reusable completion gate, not separate outstanding findings. Repeat them for each open finding.

## Resolution of finding 1 (2026-10-14)

Argument evaluation is repaired, with a design recorded in
[Evaluate step arguments once](../superpowers/specs/2026-10-14-step-argument-evaluation-design.md).
Ordinary observed values are captured per execution; stack-bound arguments retain their locations and lifetimes inside the invocation. Non-launched steps do not execute factories to format titles. Context attribution, serial observer callbacks, nullable typing, named order, return lineage, and existing by-reference/span forms are preserved.

The staff reviewer reviewed the implementation and corrections and found no remaining source-level blocker. Final local verification: zero-warning Release solution build; 1,346 passed, 5 skipped, 0 failed; 35 new behavioral cases; all four packages packed successfully. Target release: v0.3.0-beta.14.

Findings 4–6 remain open; findings 2 and 3 are resolved below. Feature opportunities remain recommendations, not an implementation commitment.

### Nullable-resource follow-up

A final nullable-resource check found CS8714 in generated claims for nullable reference parameters. The generator now validates the captured reference with a null guard before calling the resource API's notnull generic methods; the step invocation keeps its nullable signature and value. Two behavioral cases prove successful recording and null rejection before the claim/body, without warning suppression or factory reevaluation.

The staff reviewer cleared this follow-up. Final verification: 1,348 passed, 5 skipped, 0 failed; zero-warning Release build; 37 new behavioral cases in total. Beta.15 supersedes beta.14 for this correction.

## Resolution of finding 2 (2026-10-14)

Released in [v0.3.0-beta.20](https://github.com/redoz/Raun/releases/tag/v0.3.0-beta.20).
Design: [Timeout ownership and process termination](../superpowers/specs/2026-10-14-timeout-ownership-design.md).
Implementation plan: [Timeout ownership](../superpowers/plans/2026-10-14-timeout-ownership.md).

Raun now requests cancellation and retains actual invocation and cancellation-callback ownership for
60 seconds by default. Cleanup, scope disposal, and contended-resource admission wait until work stops.
The independent watchdog supervises synchronous invocation and argument preparation too. If work
survives grace, it forcibly terminates the entire test host, bypassing managed shutdown handlers.
The first cancellation cause remains authoritative, including overlapping host, step, and scenario
cancellation. Configure grace through ScenarioScheduler/RunLoop or `--cancellation-grace-ms`.

Consequences are documented in README: forced termination can leave cleanup unrun, external resources
behind, and reports/logs incomplete. Finding 3 was open at beta.20; its independent teardown deadlines are resolved below.
Finding 4's asynchronous scope disposal, finding 5's interface key dispatch, and finding 6's coverage
packaging decision also remain open.

Evidence: the premature-cleanup probe failed before the repair and passed after it. Barrier tests prove
scope and exclusive admission retention, callback lifetime, late cleanup registration, and cancellation
ordering. Real child hosts prove noncooperative async/synchronous/preparation/callback termination,
blocked ProcessExit bypass, initially cancelled zero-grace non-entry, and successful watchdog disarming.
Existing rich title/note/effect/cleanup assertions were preserved when legacy orphaning probes were
made cancellation-aware. The final staff review found no remaining source blockers.

Verification: zero-warning Release solution build; 1,380 passed, 5 skipped, 0 failed (1,385 total);
32 additional behavioral cases; all 73 report interaction checks passed; all four packages assembled.
Hosted CI run 38600542209 and release run 38601130858 passed; all four beta.20 packages were published
and uploaded to the GitHub release. NuGet indexing was pending at the immediate post-publication check.

Commits: `2a5e627` repairs runtime ownership; `aca559e` fixes the fixture's solution configuration;
`93772ec` proves probe entry under loaded runners and adds cancellation-before-invocation coverage.
Beta.18 and beta.19 tags did not publish packages because their verification gates caught fixture issues;
beta.20 is the verified release containing the fixes.

## Resolution of finding 3 (2026-10-14)

Released in [v0.3.0-beta.21](https://github.com/redoz/Raun/releases/tag/v0.3.0-beta.21).
Design: [Bounded teardown callbacks](../superpowers/specs/2026-10-14-bounded-teardown-design.md).

Each selected cleanup gets an independent 60-second deadline, then cancellation and the existing
60-second grace. The callback and its cancellation handlers must actually stop before subsequent
cleanup, scenario scope disposal, or resource admission can proceed. If either survives grace, the
watchdog kills the entire test host. Supervision starts before synchronous callback invocation.
The cleanup deadline is configurable through ScenarioScheduler/RunLoop or `--cleanup-timeout-ms`;
finite positive values are required. README documents the policy and forced-termination consequences.

Every callback receives a fresh immutable context/token, independent of the scenario and earlier
cleanup cancellation. Context views share the teardown node's logs, notes, attachments, activity,
metadata, and resource observer. Cooperative deadline expiry produces a callback-specific timeout
and lets later required cleanup run; existing ordering, optional policies, and throwing-cleanup
continuation are preserved. Errors identify the registering step and registration sequence.

Evidence: deadline and token behavior failed before the repair. Barrier tests prove callback-handler
draining, scope/exclusive-admission retention, captured-token stability, ambient-context identity,
and shared rich output/resource effects. Child hosts prove async, synchronous, and cancellation-handler
hangs terminate without starting subsequent cleanup, while cooperative expiry continues required
cleanup and reports a failed teardown. The staff reviewer found no material blockers.

Verification: zero-warning Release solution build; 1,406 passed, 5 skipped, 0 failed (1,411 total);
26 added cases; all 73 report interaction checks passed; all four packages assembled. Hosted CI
run 38615622457 and release run 38616221469 passed. All four beta.21 packages were pushed to NuGet
and uploaded to the GitHub release. NuGet indexing was pending at the immediate publication check.
Implementation commit: `71d1dae`.

Findings 4–6 remain open. Finding 4 (async scenario scope disposal) is next; interface key dispatch
and the coverage packaging decision follow separately.

## User follow-ups for later

- Resource effects tables: right-align the time column and reserve a consistent fixed width for the verb in the first column, so effect rows line up across steps and scenarios. Requested and implemented 2026-10-14: fixed-width action and time columns, right-aligned time header and values (beta.16); expanded details aligned with the step status squircle (beta.17).
