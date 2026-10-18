# Report Expansion Spacing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Do not start implementation until the user has reviewed this saved plan.

**Goal:** Give expanded scenarios their own animated external spacing without changing neighboring rows.

**Architecture:** Each scenario owns its margins and disclosure state. Expansion animates its external top and bottom margins alongside the existing body reveal; no sibling selector determines spacing. The outline and background enclose the scenario content and exclude the margins.

**Tech Stack:** Existing self-contained HTML/CSS/JavaScript report, Web Animations API, Playwright browser harnesses. No dependencies or runtime/model changes.

**Spec:** The user's instruction on 2026-10-18: “when a row expands, it should animate an extra 8px top and bottom margin ... outside the box.” The contract below makes that instruction testable.

## Contract

- Scope: scenario row expansion. Keep the existing step-detail animation; do not add external spacing to individual step or matrix rows in this change.
- Collapsed scenario: margin-top 0px; margin-bottom 0px.
- Expanded scenario: margin-top 8px; margin-bottom 8px, outside the background and outline.
- Margins are independent and additive: collapsed/collapsed = 0px; expanded/collapsed and collapsed/expanded = 8px; expanded/expanded = 16px.
- Use a column flex layout for the scenario list to prevent CSS margin collapse. No additional list gap.
- First and last expanded scenarios retain their own 8px top/bottom margins. No position-dependent exceptions.
- A collapsed neighbor's height, padding, background bounds, and margins do not change when another row expands. Its position can move as the expanded row takes space.
- Hover and focus change appearance only. They do not add margins, padding, borders that take space, or height.
- Keep the existing thin scenario boundary line at the row boundary. Remove the added floating separator and its positioning/opacity animation. Do not introduce extra separator spacing.
- Expansion and collapse use 160ms and cubic-bezier(.2,.8,.2,1) for the margins and body height together. The first rendered frame starts at the current geometry; no pre-animation jump.
- Rapid reversal samples current margin and body height. Canceling a transition must not snap to an endpoint or leave stale animation classes/styles.
- Reduced motion applies final margins and visibility immediately.
- Preserve current title padding, row typography, step density, branch dividers, Gantt backgrounds, and resource-column geometry. This is not a report restyle.

## Evidence and current causes

In src/Raun/Reporting/Html/report-template.html, .card currently transitions only margin-top. Sibling selectors assign that margin when either neighbor expands. .card::before draws a second separator outside the row. These overlapping responsibilities created the inconsistent spacing.

Measured on the real “Booking outcome” group: opening the second scenario left the first scenario at 49px high, but changed the inter-row gap from 0px to 8px and moved the next scenario's decorative separator. Tests must distinguish a neighbor's own dimensions from its position and the gap around it.

transitionScenario(card,on) already animates .scenario-reveal. Keep that entry point and integrate margin animation with its cancellation/completion behavior. Do not run unrelated animation clocks with different easing or completion assumptions.

## Global constraints

- Work in the existing isolated worktree; preserve unrelated changes.
- No source-generator, scheduler, test-discovery, or report-payload changes.
- No new package, UI framework, animation dependency, or release.
- Keep normal and compact resource matrices aligned, including the lane popover.
- Keep keyboard controls, focus, and inert handling correct throughout disclosure.

## Review focus

1. Margin collapse silently turning the required 16px expanded/expanded gap into 8px.
2. Collapsed neighbor dimensions or hover geometry changing with another row's state.
3. Interrupted open/close transitions snapping or retaining stale margins.
4. Resource lane overlays following the outer margins instead of the scenario box.
5. Reduced motion, first/last scenarios, wrapped titles, and page scrolling changing the spacing contract.

## Task 1: Pin scenario ownership and spacing with browser checks

**Files:** test/Browser/resource-matrix.cjs.

**Interfaces:** Consumes real report fixtures and .card/.card-head/.scenario-reveal; produces geometry assertions against the contract rather than specific CSS selectors.

- [ ] Replace the current “gap >= 8px” checks with all four state combinations: 0px, 8px, 8px, 16px, within 1px tolerance after settling.
- [ ] Record collapsed neighbor height, computed padding and margins, and title bounds relative to its box; assert equality after its neighbor expands and after hover/focus.
- [ ] Check first/last scenario margins and repeat at mobile width with a wrapped title.
- [ ] Pause disclosure at 0ms, 80ms, and 160ms: external margins and body height must start at current values, move together, and settle exactly. Reverse midway and verify correct final state.
- [ ] Repeat with reduced motion: no running disclosure animations; correct immediate state.
- [ ] Run the matrix browser harness and confirm these checks expose the current sibling-driven behavior before implementation.

## Task 2: Give each scenario its own margin transition

**Files:** src/Raun/Reporting/Html/report-template.html.

**Interfaces:** Preserve transitionScenario(card,on), card.scenarioAnimation, aria-expanded, and the existing report DOM/control semantics.

- [ ] Make the scenario list a column flex container with no list gap. Preserve group headings and registration order.
- [ ] Replace sibling spacing rules with .card margins 0 and expanded .card margins 8px top/bottom. Remove floating separator pseudo-element rules.
- [ ] Keep padding and the background/outline on the scenario box. Do not change collapsed row height or add internal spacing to compensate.
- [ ] Animate both owned margins together with the existing reveal height, using the specified timing/easing and a coordinated completion/cancellation path. Retain current measured geometry on reversal; clear temporary styles on completion.
- [ ] Preserve immediate reduced-motion behavior and unchanged geometry on hover/focus.
- [ ] Run the Task 1 checks and the existing scenario-flow harness; fix causes without weakening branch, timing, dependency, or matrix assertions.

## Task 3: Verify the complete interaction and refresh the preview

**Files:** test/Browser/resource-matrix.cjs, test/Browser/scenario-flow.cjs; report template only if a regression needs correction.

- [ ] Verify the lane overlay aligns with the scenario box, excluding margins, during and after expansion. Opening the popover must not change the report's geometry.
- [ ] Run: node test/Browser/resource-matrix.cjs artifacts/latest-report/raun-report.html.
- [ ] Run: node test/Browser/scenario-flow.cjs artifacts/latest-report/raun-report.html.
- [ ] Inspect one desktop and one mobile reference containing collapsed/expanded neighbors, consecutive expanded scenarios, branches, notes, and expanded evidence.
- [ ] Refresh the local report from the verified template and existing real-run payload; no .NET rerun is needed for this presentation-only change.
- [ ] Run git diff --check. Confirm the implementation removes sibling exceptions and does not change internal row spacing.
- [ ] Commit and push once verified. No release.

## Deferred work

Combining condition steps with If headings is a separate feature. Do not change condition rendering, resource-effect placement, or branch grouping while fixing scenario spacing.

## Status

Planning only. The report implementation and preview have not been changed by this plan. No worker has been dispatched. Review this contract before starting Task 1.
