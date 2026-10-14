# Evaluate step arguments once

Date: 2026-10-14
Status: implemented and reviewed; final verification recorded in the staff review

## Problem and contract

Staff review demonstrated that one valid argument factory is evaluated independently by titles, resource claims, and the operation. Repeated placeholders amplify this. Skipped-step formatting also executes factories. This violates the documented once-per-consuming-step contract and can invalidate resource isolation and evidence.

## Design

The generator emits an optional argument preparation delegate when titles or claims consume supplied arguments. Calls with no argument metadata retain their original once-only invocation path. It assigns each argument to a local of its bound converted type, in authored order, then returns boxed captured values. Generated invocation, claims, lineage, and runtime titles read those slots through IStepInputs.GetArgument<T>. Original expressions remain available only for compile-time discovery-name folding.

The scheduler prepares only launched nodes, inside the step execution/failure boundary, before runtime title formatting and the starting callback. Preparation runs inside the attached consuming-step context and activity, retaining its logs, effects, and cleanup registrations. The symbolic initial name is refined after capture. Starting and finishing observer callbacks remain serial. Captured inputs wrap producer inputs and live for that node execution only; definitions hold no mutable state. Preparation failure is a consuming-step failure, with no invocation or generated claims. Non-launched nodes and discovery have no argument slots, so formatters use symbolic templates without executing factories. Constants and direct producer values may provide explicitly enabled safe fallbacks; conversions with user-defined operators cannot. Anonymous inferred types use a non-executing lambda type witness, while named capture types preserve nested nullable annotations. Existing caller-built nodes without preparation keep their behavior.

Calls containing by-reference or ref-struct inputs use a stack-bound path inside Invoke. Typed locals/ref aliases evaluate values and locations once in authored order. The title is frozen before claims/the operation; its starting notification stays symbolic and terminal/timeout reporting uses the captured title. Input resource verbs synchronously record into the ledger and return completed ValueTasks, so this path completes those claims synchronously before invoking. This relies on ResourceContext's existing no-wait recording contract; a future asynchronous resource-claim implementation must revisit this path. Post-call lineage snapshots ref values after the synchronous operation call and before awaiting its Task. Such parameters cannot be retained in an async operation body, and generated aliases never survive an await. Out discards/declarations get a typed scratch local. Implicit in arguments preserve real locations, while fields on returned struct values retain temporary semantics.

Preparation is synchronous, as supported arguments cannot await. It is measured as step execution; like synchronous operation prefixes it cannot be forcibly preempted. This patch does not attempt to solve the separately recorded noncooperative timeout policy.

Nullable captured reference resources are validated before generic resource recording; invocation retains the original nullable parameter contract. Preserve implicit conversions, nullable values, contextual lambdas/initializers, named evaluation order, generic calls, optional-title eligibility, constant discovery names, input claims before the call, and return-dependent claims afterward. Preserve source mapping on the argument preparation statements.

## Alternatives

- Require pure arguments: rejected; ordinary factories and constructors are explicitly supported, and purity cannot be proven generally.
- Cache only title expressions: rejected; claims and invocation would still disagree and repeated claims still duplicate work.
- Lazily cache during formatting: rejected; discovery/skipped formatting would execute user code and formatting could swallow operation failures.
- Store captures on ScenarioNode: rejected; definitions are reused and may run concurrently.
- Add synthetic graph nodes for each argument: rejected; expands graph/report/filter surface for compiler plumbing.

## Verification

Behavioral counter tests for shared title/claim/call values, repeated placeholders, throwing and skipped factories; additional tests for named order, implicit conversion, object identity and return lineage, non-taken/unselected steps, and definition reuse. Run generator/runtime suites, inspect snapshot changes, then zero-warning solution build and whole-solution tests.
