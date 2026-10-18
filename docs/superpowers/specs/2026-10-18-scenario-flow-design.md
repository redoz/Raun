# Scenario flow: timing and branches

Approved 2026-10-18. Replace the unreadable dependency pixel matrix with a shared scenario-time Gantt strip integrated in each step row. Timing communicates observed overlap, not potential concurrency. Preserve exact dependency inspection by name on demand, including guard, ordering and merge edges. Use the existing chevron.

Carry condition arm labels and ordered guards into the report payload. Group contiguous rows by their guard path, preserving authored order and every node, including synthetic merges and lifecycle steps. Nested if/switch regions use subtle neutral rails and source arm labels. Collapse only branches known to be not taken; leave failures, filtered and blocked steps inspectable. Branch disclosure must not toggle step details. No invented parallel or join captions.

Keep resource columns, evidence popovers, source links and keyboard navigation. The resource matrix must find step rows through branch wrappers and its expanded overlay must mirror visible row positions, including branch-heading gaps. Gantt bars use finite real offsets/durations, a common axis and a minimum visible zero-duration mark; skipped rows have no execution bar. Hover/focus shows exact timing and direct predecessors; clicking named predecessors reveals collapsed ancestors and focuses their steps. Mobile uses a narrower shared strip without horizontal page overflow. Reduced-motion users get no animation.

Rejected: git-log paths and dependency pixels require decoding; textual parallel groups add noise; inferring branches from titles loses source semantics; nesting whole resource grids would misalign columns.

Implementation checklist:
- [x] Model regression proves arm labels and ordered guard paths survive serialization.
- [x] Browser regressions cover timing geometry, skipped bars, nested branches, disclosure, dependency navigation and matrix overlay alignment.
- [x] Implement metadata and report layout; remove obsolete pixel code and tests while retaining graph correctness assertions through named edges.
- [x] Build, runtime tests, browser checks and real appointment report.
- [x] One final reviewer, fix material findings, commit and push. No release.

Verification: full solution 1,752 tests (1,745 passed, 7 expected branch skips); zero-warning build; 72 report JavaScript tests; both report browser harnesses. Final independent staff review found lifecycle navigation and Escape focus issues; both corrected and covered by browser regressions. Zero-duration marks and nested resource-overlay alignment also verified.

2026-10-18 refinement: render the shared Gantt axis as a muted background across each title row, stopping at the resource matrix. Keep text, resource links and log disclosures above the background. The duration is the timing/prerequisite button; no separate timing column. Verify full-row bounds, aligned axes, matrix exclusion and keyboard interactions.
