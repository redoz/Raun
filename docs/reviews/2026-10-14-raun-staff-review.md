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

## Resolution of finding 1 (2026-10-14)

Argument evaluation is repaired, with a design recorded in
[Evaluate step arguments once](../superpowers/specs/2026-10-14-step-argument-evaluation-design.md).
Ordinary observed values are captured per execution; stack-bound arguments retain their locations and lifetimes inside the invocation. Non-launched steps do not execute factories to format titles. Context attribution, serial observer callbacks, nullable typing, named order, return lineage, and existing by-reference/span forms are preserved.

The staff reviewer reviewed the implementation and corrections and found no remaining source-level blocker. Final local verification: zero-warning Release solution build; 1,346 passed, 5 skipped, 0 failed; 35 new behavioral cases; all four packages packed successfully. Target release: v0.3.0-beta.14.

Findings 2–6 remain open. Feature opportunities remain recommendations, not an implementation commitment.

### Nullable-resource follow-up

A final nullable-resource check found CS8714 in generated claims for nullable reference parameters. The generator now validates the captured reference with a null guard before calling the resource API's notnull generic methods; the step invocation keeps its nullable signature and value. Two behavioral cases prove successful recording and null rejection before the claim/body, without warning suppression or factory reevaluation.

The staff reviewer cleared this follow-up. Final verification: 1,348 passed, 5 skipped, 0 failed; zero-warning Release build; 37 new behavioral cases in total. Beta.15 supersedes beta.14 for this correction.
