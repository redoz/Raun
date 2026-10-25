# Unified coverage outcomes

Status: design, agreed in conversation 2026-10-25.

## Intent

The Coverage view answers "how well is this tested?" in a different vocabulary for every kind of
branch. The Sankey's outcome column has seven nodes, three of them kind-specific ("Throw / handler
entered", "Expression / filter reached", "Library checks: unavailable"), so the same quality of
evidence lands in different boxes depending on the branch type. The counters, the summary score,
the table columns, the bars ("exact / coarse / filters" families) and the labels carry the same
split. Patrik: every branch kind should unify on one small set of outcomes.

Separately, Coverlet records hits per IL branch path, which is better evidence than the Microsoft
XML's per-range covered flag, yet expression branches (`?:`, `?.`, `??`, `&&`, `||`, switch
expressions) with full Coverlet path evidence still resolve to `Reached` ("can't tell") where an
`if` with the same evidence resolves to `Complete`. Patrik: make this as good as we can.

## Goals

- One outcome set for every branch kind, used everywhere: the Sankey, counters, summary score,
  table columns, bars, labels, source-line highlighting and the band selection filter.
- Coverlet path evidence gives expression branches real outcomes.
- Nothing claims more than the evidence shows.
- Per-kind nuance ("the exception filter ran; its outcomes cannot be determined") survives, but as
  the *reason* on an individual branch, not as an outcome category.

## Non-goals

- Changing which branches are inventoried or how they are classified (argument checks, error
  handling, null propagation, logging, logic).
- Reading per-path data out of the Microsoft XML (it has none); trusting its `covered="yes"` for a
  shared range as proof of every outcome (blocks can map to sequence points ambiguously).
- Using Coverlet evidence where `CoverletBranchEvidence.PathHits` declines today (several source
  decisions or several IL branch points on one line). That pairing rule stays exactly as it is.
- The collector's own "Total" branch-path percentage.

## Anti-goals

- An outcome that means "partly covered" for one branch kind and "can't tell" for another.
- Folding "can't tell" into covered or partly covered.
- Keeping the old counters alongside the new ones.

## The outcome set

| Outcome (key) | Label | Meaning |
|---|---|---|
| `covered` | Covered | Every outcome the branch has was observed. |
| `partial` | Partly covered | The branch ran, and at least one of its outcomes was shown not to run. |
| `uncovered` | Not covered | The branch never ran. |
| `unknown` | Can't tell | The evidence cannot establish which outcomes ran. |

Mapping from `EvidenceStatus`:

| Status | Outcome | Reason (for `unknown`) |
|---|---|---|
| `Complete` | covered | |
| `Exercised` for a handler (the catch was entered; the catch is its only outcome) | covered | |
| Guard (see "Guards" below) | covered / partial / uncovered | `untriggered` on partial when the condition ran and the throw did not |
| `Incomplete` | partial | |
| `Partial` (expression or filter range partly executed), when this branch is the only inventoried branch inside that range | partial | |
| `Partial`, when the range holds other branches too (the missing block may be theirs) | unknown | `shared-range` |
| `NotReached` | uncovered | |
| `Reached` (expression range ran, no path data) | unknown | `expression` |
| `Evaluated` (exception filter ran) | unknown | `filter` |
| `Unknown` | unknown | `unresolved` |
| `Unmeasurable` (check inside a .NET library) | unknown | `library` |

"Measurable" = covered + partial + uncovered. Percentages are covered / measurable.

## Guards (Patrik's choice: score the rejection)

A guard (`if (x is null) throw ...`, argument checks and other throw-only `if`s) records only
whether its throw ran. Scored by itself, "throw never ran" would read as "never ran" for every
check whose condition ran and passed. Guards are therefore scored on the question that matters for
a check, "was the bad-input case tested?", using the condition as well as the throw:

- With Microsoft XML ranges: throw ran → covered; condition ran but the throw never did →
  partial ("never tested with bad input"); condition never ran → uncovered; condition range not
  resolvable → unknown (`unresolved`).
- With Coverlet path evidence (`PathHits` for the guard's single IL branch point): both paths hit
  → covered; exactly one hit → partial; none → uncovered. (Strict here because both paths are
  observable.)

`EvidenceStatus` gains whatever value the resolver needs to express "condition ran, throw did not"
(builder's choice), so the mapping stays one function.

## Coverlet expression evidence

In `CoverageResolver`, when `PathHits` returns more than one path for a Decision **or Expression**
branch, both use the same rule: all zero → `NotReached`, all positive → `Complete`, otherwise
`Incomplete`. `PathHits` only returns data when the line has exactly one source decision and
exactly one IL branch point, so "every path hit" proves every outcome of that branch.

## Model changes (public, `Raun.Reporting.Coverage`)

- `ReportCoverageCounts` becomes `Inventory`, `Covered`, `Partial`, `Uncovered`, `Unknown`. All
  kind-specific counters are removed (beta; breaking on purpose).
- `ReportCoverageBranch.Outcome` uses the four keys above.
- `ReportCoverageBranch` gains `Reason` (`string?`): `expression`, `filter`, `shared-range`,
  `unresolved`, `library` for `unknown`; `untriggered` for a partly covered check whose throw never ran (amended 2026-10-25 after the pre-merge review: the explanation comes from the evidence, not the category); otherwise null.

## Report changes

- Sankey: the outcome column is the four outcomes, in the table's order (covered, partly covered,
  not covered, can't tell). Branch-kind column unchanged.
- Summary "Logical" score: Logic category covered / measurable (it now includes logic expressions
  and guards with known outcomes, not only decisions). Tooltip wording follows.
- Table columns: "Logic decisions" → "Logic covered" (fraction), "Not reached" → "Not covered",
  "Partly tested" → "Partly covered". Sorting follows the new values.
- Ranking (`coverageRank`): fraction covered / measurable, then missing (partial + uncovered),
  then unknown. The guard/decision/expression family priority goes away.
- Bars: one bar with four segments. Labels: "N of M covered", "N partly covered", "N not covered",
  "N can't tell"; the can't-tell line explains generically and points at the source lines.
- Source lines: highlighting and the band filter use the outcome key; a branch's tooltip shows
  kind → outcome, plus the reason's explanation when the outcome is `unknown` (the existing
  per-kind explanation texts move here, keyed by reason).
- CSS: one colour per outcome, from the existing coverage palette (covered, partial, missed,
  unknown/hatched). Remove `.coverage-reached`, `.coverage-evaluated`, `.coverage-filter-partial`,
  `.coverage-expression-missed`, `.coverage-filter-missed` and the helper class.
- Also on the old counters, all moving to the four outcomes (found by the builder check):
  `coverageLogicBar` and the row score text beside it; the row number cells
  (`notReached`/`incomplete`); the `buildCoverageScope` heading ("N of M logic decisions fully
  tested"); the path-hits panel and the `coverageInBand` / method-source filters (match on the new
  outcome keys); `coverageRowTitle` (follows `coverageLabels`, needs a test); the run-header
  tooltip ("Expressions and argument checks are excluded" becomes false).
- Path hits: the resolver may decide an outcome from Coverlet paths that the tooltip does not
  display (`CoverageSourceBuilder` shows path counts only for modules with frequency counts,
  because other modules' hits are 0/1, not counts). Unchanged: the line still shows the outcome.
  *(Claude's call.)*
- Reasons also include `shared-range` (see the mapping table); every reason has one explanation
  text in the template.

## Tests

Behavioral tests first, at each layer:

- Resolver: a single `??` / `?:` / `?.` with Coverlet paths all hit → covered; one path hit →
  partial; none → uncovered. Microsoft XML range-only evidence for the same expression → unknown
  with reason `expression`. Two decisions on one line with Coverlet data → unchanged (no path
  evidence used).
- Guards: throw ran → covered; condition ran, no throw → partial; condition never ran →
  uncovered; Coverlet both / one / no paths → covered / partial / uncovered.
- Shared range: a partly executed range holding two inventoried branches → unknown
  (`shared-range`) for both.
- Mapping: every `EvidenceStatus` value maps to the table above (exhaustive test over the enum).
- Summary: counts per category add up (`Inventory` = covered + partial + uncovered + unknown).
- Report: Sankey has at most four outcome nodes for a mixed-kind run; selecting an outcome band
  filters source lines by outcome; a filter branch's tooltip carries the filter explanation.
- Existing JS interaction and browser tests updated to the new vocabulary.
- `test/Raun.PackageTests/PackageConsumer.VerifyCoverage` checks the old count names; it moves to
  the new counts and must accept `unknown` where the consumer's evidence is range-only.
  `CoverageSourceTests` (asserts `"missed"`) and `CoverageSummaryTests` (old counters) are rewritten.

## Left to the builder

- Exact label and tooltip wording beyond what is stated.
- CSS colour assignment within the existing palette.
- Whether the four-outcome order/labels live in one shared JS constant (expected).
