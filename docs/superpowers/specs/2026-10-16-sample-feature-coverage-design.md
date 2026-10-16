# Natural sample feature coverage

Date: 2026-10-16
Status: implemented and verified; final review findings resolved.

## Goal and scope

Make implemented capabilities discoverable through realistic runnable examples. Preserve the
existing public API and all guard/merge/filter checks. No runtime changes or release.

- Registry booking: an external row and a domain Patient share a stable ID distinct from its name.
  Approve the patient in an isolated registry, book by string key through typed Booked metadata,
  and read back the stored booking. Retain original evidence and no borrowed key-only snapshot.
- Conditional booking: open slots book, held slots waitlist or refuse according to patient choice.
  Record Create/Reference/Consume imperatively only for the successful wrapped result.
- Titles: use optional city variants on patient setup and formatted member paths on confirmation.
- GettingStarted: a minimal generated-Main project in the solution, leaving custom hosts useful.
- Document the feature map and configuration/failure capabilities better demonstrated elsewhere.

## Alternatives

Separate framework-themed scenarios for each API were rejected: they miss interactions and hide
real domain intent. Adding EF just to demonstrate an external row was rejected: the mapping only
needs an ordinary foreign shape. Forcing failures, process kills and every overload into samples
was rejected: dedicated tests and recipes give clearer evidence. Converting the existing hosts to
generated Main would remove their simulated-time/Aspire purposes, so add a small default example.

## Verification

Real report assertions pin canonical resource count/key, source-specific row/domain snapshots,
remembered labels on typed key-only relationships and audit, correct edge endpoints/kinds, title
selection/member formatting, and successful/unsuccessful outcome evidence. Keep all browser checks.
The new generated-host sample runs its own domain assertions through MTP in the solution suite.
Update exact sample totals without weakening the six-node filtered scenario assertion.


## Completion evidence

- Full solution: 1,577 total, 1,570 passed, seven expected branch skips, zero failures.
- AppointmentTests: 112 MTP nodes (105 passed, seven skipped); the filtered booking stays six nodes.
- GettingStarted: six passed through its generated host, also included in the solution/CI run.
- Real report browser checks and 72 JavaScript tests pass; desktop output inspected.
- Initial report checks failed on the missing registry journey before the sample migration.
- Final review found that unsuccessful outcomes were not distinguished. Added the expected passed
  assertion and two not-taken alternatives for each outcome, including position/refusal text.
  Mutation proof: forcing every held-slot outcome to Waitlisted fails the refusal assertion;
  restoring the correct implementation and rerunning passes. No deferred review findings.
- Fresh restore exposed NU1902 for existing MessagePack 2.5.302. Applied the upstream 2.5.303 patch
  in Aspire and its AppHost, with both RID locks. Removed incidental floating OpenTelemetry drift.
  Forced locked restore and solution build pass with zero warnings/errors; resolved assets retain
  OpenTelemetry 1.19.1 and MessagePack 2.5.303. The final Aspire rerun passes all 16 nodes.
  Local Raun.Aspire packaging succeeds and its nuspec requires the patched dependency.

Execution stayed in the existing isolated managed worktree. The original user checkout is preserved.
The sample guide explicitly separates exercised journeys from configuration recipes and dedicated
failure tests. No new public API, release, tag, or package publication.


## Follow-up: resource mentions in titles

Raw name/ID arguments and scalar member paths format as ordinary text. The samples now use
return-aware creation titles and resource-valued member paths, with readable Slot/Appointment
labels. The registry journey renders eight real resource references, including both registration
name/ID, the slot, and the audited booking. Resource-valued paths preserve identity; plain import
counts still demonstrate scalar member formatting. README examples document that distinction.

The new report assertions failed on the old registration title. Real model and DOM checks pass
with all eight references; clicking the registration ID highlights all five patient mentions.
AppointmentTests passes 105 nodes with seven expected skips, GettingStarted passes six, and the
137 MTP tests (including the unchanged six-node filter guard) and 72 report JavaScript tests pass.
Solution Release build has zero warnings/errors. No runtime formatting or renderer changes.
