# Resource model final review

Reviewed: `f493024..3537a48` by an independent Astra High staff reviewer. Scope: runtime contracts,
compiler lowering and diagnostics, editor-facing metadata, reports, samples, and current documentation.
The code-fix project has its own branch and review.

## Verdict

The four-effect model and independent role-named links are sound. Links do not manufacture access
claims. Return-owned links follow successful return recording. Scenario admission still encloses
teardown and asynchronous scope disposal. Two compiler boundary defects needed corrections before landing.

## Findings and resolution

1. **Important: raw links could borrow a derived action's role.** `Link<CreatedAttribute>("Patient")`
   was accepted when the applied action was Booked, despite Created declaring no Patient role.
   Binding now resolves the declared role on the explicitly named owner. A derived source action can
   still supply an inherited role. The rejection regression failed before the fix; both the rejection
   and valid inheritance execution tests now pass.
2. **Important: collision validation depended on declaration order.** An ordinary Patient property
   before PatientAttribute escaped source validation; referenced metadata exposed another ordering
   difference. The compiler now discovers nested roles before checking ordinary members, like
   reflection. Tests cover both orders, referenced assemblies, and runtime metadata rejection. Both
   previously failing cases now pass.
3. **Public contract documentation:** Subject.Return described a target rather than a source, and
   LockMode listed retired shared effects. These comments now describe return-source selection and
   the four CRUD effects. Treated as required contract correctness because the requested model docs
   must explain the new meanings accurately.

## Decisions retained

- Rename the old observation record to ResourceObservation so ResourceEffect unambiguously names
  the CRUD enum. Cost: repository API migration; no external compatibility requirement was retained.
- Explicitly annotated opaque external objects can use runtime fluent mappings. Known canonical
  mismatches and untyped scalar inputs remain compiler errors; unknown mappings are checked by the
  runtime recorder. Editor matching does not guess an unknown identity. Cost: an invalid opaque
  mapping is reported during execution rather than compilation.
- Core owns runtime/compiler semantics. The separate code-fix branch owns editor projects and their
  solution/package inclusion. Cost: an explicit integration step and shared compiler facade.
- Correct the public-comment finding in this fix pass because it taught the opposite link direction.
  Cost: documentation-only wording changes.

## Behaviors considered and set aside

- A suspected identity-only-owner defect was disproved against actual assemblies: IResourceAlternate
  inherits IResource, and the facade recognizes the owner.
- Opaque fluent mappings follow the explicit runtime-validation decision above.
- Unchanged reservation and scheduler architecture was not redesigned. Existing lifetime tests and
  the inspected completion boundary retain disposal coverage.
- Native Visual Studio/ReSharper interaction was not exercised by this core review. No native IDE
  validation is claimed for the separately developed code-fix project.

## Verification

Before review: zero-warning solution build; 1751 tests, 1744 passed and seven expected branch skips;
13 focused CRUD/link tests; 72 report JavaScript tests; real appointment report and both resource-matrix
and scenario-flow browser harnesses; real Aspire sample through the solution suite.

After fixes: all 23 relationship generator tests pass. The final whole-solution run passed:
1756 total, 1749 passed, seven expected branch skips, zero failures. No unresolved review findings or deferred minors.
