# Handoff 4 — activity-diagram visualization: visual LOCKED at v17; next focus = line labels

Date: 2026-06-20 (session 4)
Status: **Core visual is LOCKED at `v17`.** The whole passthrough-glyph sub-system was explored and then
**deliberately dropped** — wall crossings are now just the object line through a clean gap in the wall (the gap
*is* the port). Next focus: **the line (edge) labels** — the `create/read/edit/delete` action verbs on the
object-flow edges. Then: collapse-tier mock → spec → plan → TDD.
Continues: `…-handoff-3.md` (s3), `…-handoff-2.md` (s2), `…-handoff.md` (s1). Read this one first; reach back to
handoff-2/3 for the fork-timeline "special sauce" rationale and the full mockup history.

How to start the next session: read this file, open the locked mock
`docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/v17-gap-crossings.html` in a browser,
then pick up at **§3 (line labels)** using the brainstorming visual companion (§6), headless-rendering every mock
before pushing.

---

## 1. What happened this session (v15 → v17)

Started from the converged `v14` and applied handoff-3's four tweaks, then went deep on the wall-crossing port —
and concluded by **removing it**.

- **v15** — applied handoff-3 §3: `)( → ][` bracket passthrough with a straight crossing stub; arrowheads;
  ±30° off-axis arrivals; Source Serif 4 carried in.
- **v16** — smaller/rounded `][` tunnel hugging the wall; **object-flow wire made one uniform width** in & out of
  the cell (the old thin-inside→thick-outside "bond wire" was disappearing against the panel — that locked element
  is now **retired**); timeline lane labels dropped 7px→6px.
- **Passthrough-glyph exploration** (the big detour): mocked the crossing as a *circular-port cross-section / a
  grommet seated in a hole* — ring/donut/lens/barrel/grommet variants, then `][`-bracket variants swept on
  gap/radius/lips/weight, then a **corrected grommet model** (grey wall flush to the spine, lips overhang the grey
  like a flange, hole kept open inside), then a circle+dot ("wire end-on") option. Rendered them **at actual
  diagram scale next to the ring/disc node ports**.
- **Decision: drop the glyph entirely.** The layout/exit rules already mark the crossing, so a glyph just competes
  with the line for a few pixels and risks clashing with the `ring = input` node port. → **v17**.
- **v17** — **gap-only wall crossings** (line passes straight through a clean, narrowed gap in the join/side wall;
  no glyph); **arrowheads aim their centreline at the port centre** (straight tail into each ring, base centered
  on the line, tip just outside). User: "this is all good." **Locked.**

Tooling note: kept the **headless render loop** (write standalone to `_preview/`, `npx playwright screenshot
--browser=chromium`, inspect the PNG). Added a **zoom-inspection trick**: extract the diagram `<svg>` inner markup
and re-wrap it in tight `viewBox`es to render any region at 10–14× — catches geometry/arrowhead bugs the
full-page shot hides. Use both.

---

## 2. The LOCKED visual = `v17` (describe THIS in the spec)

A **flat, dark, top-down activity diagram**, per scenario. Near-square corners (`rx 1–3`), thin strokes, fine
serif, emphasis on hover/active only. Bends UML where it buys clarity.

### Frame & control flow
- **Horizontal Given/When/Then swimlane bands**, full width, flat tint ~`.06–.07`, each with a rotated phase
  label + a thin (`2.5px`) colored tab. Hues: Given `#3f82e6`, When `#9a6ae0`, Then `#1aa48d`. **Band heights are
  content-driven** (Given is given extra height so object cards sit clear below the fork).
- **Control-flow spine straight down the centre axis** (hairline grey `#5c6571`, `1px`): initial node (filled
  circle) → action nodes → decision diamond → merge diamond → final node (ring + core). **Control owns the CENTRE
  of every node edge** and meets the fork/join bars directly.
- **Action nodes** = content-sized boxes (label + padding, min-width floor), near-square (`rx 3`), phase-tinted.
- **Decision/merge** = dark diamonds, thin stroke `#544470`, tiny label. Branch connectors are **splines** with a
  plain `Yes`/`No` label (no brackets). Curved branches end with a **short straight tail** so the arrowhead base
  sits square on the line (see §4 arrowhead rule).

### The fork = a packaged "cell" with the inline timeline (unchanged special sauce)
- 4-walled cell: heavy slate **fork bar** (top) + **join bar** (bottom) with ruler ticks, + lighter **left/right
  walls** (`#5f6873` walls over a `#161b22` panel).
- Inside: the **real overview-timeline machinery** (`report-template.html`'s `.timeline/.tl-row/.bar` with
  `niceAxis`/`fmtTick` ruler + `ms` gutter, gridlines, phase-hued lane bars carrying the white `G/W/T` chip +
  serif label). One lane per parallel step by `offsetMs`/`durationMs`. Cropped to the fork's `max(offset+duration)`
  and centred under the spine.
- **Each lane has a disc port** at its production end.
- **Objects leave the cell through a GAP in a wall** (see §2 crossings): straight-down through the **join** wall
  for objects consumed below; **out a side wall + down-loop** for divergent objects (`Database` exits the **left**
  wall, runs down the left margin to a Then assertion).

### Wall crossing = a clean gap (NO glyph) — this is the v17 change
- Where a data line crosses a wall, the **wall simply has a clean gap and the line passes straight through it**.
  The gap *is* the port. Continuous, **perpendicular**, full object-flow weight. No grommet / no `][` / no
  circle-dot. In the mock the join-wall gaps were narrowed to ~4px so the hole reads as deliberate (line ~1.3 with
  ~1.3 clearance each side); the left-wall (database) gap is the same idea rotated 90°.

### Object flow = entity cards + action-labelled edges
- **Each object = a card**: colored **type-header band over a dark identifier body** (e.g. `PATIENT` over `Jane`;
  `#131922` body, `#2a313c` border, `rx 1`).
- Flow runs **through** the card: `producer disc → [wall gap if it crosses a wall] → object card → consumer`.
- **Edge label = the action verb** (`create/read/edit/delete`), italic serif, in the object's (brightened) color.
  **← THIS IS THE NEXT FOCUS (§3). It is the one under-designed part of the picture.**
- **Edges are S-curves**; leave/arrive relaxed to **off-axis up to ~30°**.
- **Object-flow line is ONE uniform width** (`1.3` in the mock) the whole way — no thin/thick.
- **Ports**: input = hollow **ring**; output = filled **disc + faint halo**; colored per object. **Input arrows
  aim their centreline at the ring centre**, base centered on the line, **tip just outside** the ring.
- **Data ports clamp to a fixed inset from the node edge and stack inward**; control owns centre; **same inset for
  in & out** so an output lines up above the matching input. When a side runs out of room → collapse (§5).

### Style / colors (dark mock; spec must ALSO define the LIGHT palette)
Object colors are illustrative — the real renderer assigns per-type colors from the existing `PALETTE`/`typeColor`
machinery: Patient `#e08544`, Slot `#5cb877`, Appointment `#e06aa0`, Database `#7c97f0`; slate walls `#5f6873`;
control grey `#5c6571`.

---

## 3. NEXT FOCUS — line (edge) labels  ← START HERE

The action-verb labels on the object-flow edges are the least-designed element and the user wants to nail them
next. **This is a fresh brainstorm/iterate-in-the-companion task** (same loop as the port work).

### Current state in `v17` (what to improve)
- CSS: `.vb { font-weight:500; font-size:6px; font-style:italic; font-family:var(--dfont) /* Source Serif 4 */; }`
- Markup: a single `<g text-anchor="middle">` of hand-placed `<text class="vb" x="…" y="…" fill="…">verb</text>`,
  one per edge, **horizontal**, color = a **brightened** version of the object color (e.g. patient create
  `#e08544`, its read `#e0915f`; slot `#5cb877`/`#79c692`; appointment `#e06aa0`/`#e687b4`; database
  `#7c97f0`/`#97acf3`). 8 labels total in the demo (create/read per object).
- They're **manually positioned** — fine for a mock, but the renderer must place them from geometry.

### Open questions to explore + lock with the user
1. **Placement** along/near the curve: visual midpoint? a fixed fraction toward the producer/consumer? offset
   perpendicular off the line by a few px so it doesn't sit on the stroke? Decide the rule.
2. **Collision avoidance**: labels must not collide with cards, band edges, the timeline cell, the control spine,
   other edges, or each other. Need a simple de-overlap strategy (nudge along normal / along the curve / pick the
   least-crowded side).
3. **Orientation**: horizontal (current) vs. **rotated to follow the edge tangent**. Rotated reads as "on the
   wire" but can get upside-down on down-loops; horizontal is safe but can detach from its edge. Mock both.
4. **Legibility over busy areas**: a subtle **halo/knockout** (panel-colored pill or blurred stroke) so a label
   stays readable where it overlaps a band/card/another line. Must work in **both** light & dark palettes.
5. **Leader**: if a label has to sit far from its edge to stay clear, does it get a hairline leader back to the
   line, or do we just keep it close?
6. **Which verb when a resource has several events** (`create` then `read` then `edit`…): one label per edge
   (producer→consumer) carrying that hop's verb, vs. an aggregated label. Model has both
   `resources[].events[]{verb,offsetMs,stepId}` and `steps[].effects[]{verb,type,key,…}`.
7. **Density / always-on vs hover**: with many edges the labels could clutter; consider always-on for ≤N, fade or
   hover beyond that. Ties into collapse (§5) — aggregated edges carry an aggregate verb/count.
8. **Color/weight**: keep brightened-object-color italic serif, or tone down? Confirm contrast on light theme.

Deliver: a few companion mocks (placement rule, orientation, halo treatment, a crowded case), headless-verified,
then lock with the user. Then move to §5.

---

## 4. LOCKED decisions (updated this session)

- **Wall crossing = gap-only, NO glyph.** (Supersedes the entire grommet/`][`/circle-dot exploration — all
  rejected. The gap in the wall is the port; the object line passes straight through.)
- **Object-flow line = one uniform width** in & out of the cell. (Retires the old thin-bond-wire→thick-outside
  element from handoff-3's locked list.)
- **Arrowheads:** the **base sits centered on the line** (curved arrivals end with a short straight tail so it
  lands square, not skewed off the tangent); for arrivals at a node port the **centreline aims at the ring/port
  centre**; **tip just outside** the port.
- **Serif:** Source Serif 4 (base64 woff2, embedded in the build).
- **Architecture / scope:** activity diagram is the single per-scenario headline view; the existing **drill panel
  stays** for per-step detail; the overview timeline is **reused only inside forks**. No separate full-scenario
  timeline. (Confirmed.)
- **Ports:** ring = input, disc + halo = output, colored per object.
- **Connector grammar:** every connector = an S-line + a label + a color; decision labels `Yes`/`No`.
- **Layout rules:** control owns centre; data clamps to a fixed edge inset and stacks (same inset in/out);
  straight-down or side-exit routing; **perpendicular wall crossings**; off-axis arrivals/departures allowed up to
  **~30°**.
- **Collapse:** deferred; model agreed (§5).

---

## 5. Collapse tiers — agreed model (mock AFTER line labels, then spec it)

Density decided **per producer→consumer edge bundle**, three automatic tiers with **click-to-expand**:
1. **Expanded** (≤ ~4 objects): individual entity cards, as `v17`.
2. **Grouped by type** (exceeds the card threshold): **one card per type with a count**, drawn as a small
   **stack** (offset cards behind) — e.g. `PATIENT ×12` — one edge per type carrying the aggregate verb.
3. **Fully collapsed** (too many *types*, or counts still too large): a single **bundle conduit** between the two
   steps — one thicker edge + a neutral count chip (e.g. `23 objects`); breakdown moves to hover / the drill
   panel.
Thresholds to tune (defaults): `>4 objects → group by type`; `>4 type-groups or >~12 total → full bundle`. Any
bundle is **click-to-expand one tier**; the drill panel always lists the full set.

---

## 6. Companion + headless self-verify loop (USE BOTH)

**Brainstorming visual companion** — session dir `.superpowers/brainstorm/9542-1781954515/` (gitignored). The user
keeps a tab open. If the server is down, restart with the **same `--project-dir`** (reuses the port; the tab
auto-reconnects). On Windows run in background:
```
bash "/c/Users/redoz/.claude/plugins/cache/claude-plugins-official/superpowers/6.0.0/skills/brainstorming/scripts/start-server.sh" --project-dir "/c/dev/raun" --open
```
then read `.superpowers/brainstorm/9542-1781954515/state/server-info` for the URL. **Push a screen** by writing a
fragment HTML into `…/content/` (server serves the newest file). Current newest = `v17-gap-crossings.html`.

**Headless self-verify loop** — write a standalone (dark page wrapper + the Google-Fonts `<link>`) into
`…/_preview/`, then:
```
npx playwright screenshot --browser=chromium --full-page --viewport-size=900,1500 _preview/NN-standalone.html NN.png
```
Inspect the PNG; fix; then derive the companion fragment (strip the DOCTYPE/`<body>` wrapper — there's a one-line
`perl` for it in the session's bash history) and copy into `content/`. **Chromium is installed; the Playwright
*MCP* defaults to Chrome which is NOT installed — use the CLI `--browser=chromium`.**

**Zoom-inspection trick** (high-res geometry checks): `INNER=$(sed -n '47,195p' _preview/v15-standalone.html)`,
then drop `$INNER` into several `<svg viewBox="x y w h">` at large pixel widths to render any region at 10–14×.

**Working files (note the stale name):** the live v17 standalone is **`_preview/v15-standalone.html`** (the
filename is stale from v14 — its *content is v17*). The committed, browser-openable copy is
`docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/v17-gap-crossings.html`. Other comparison
mocks from this session live in `_preview/` (brackets/brackets2/brackets3/ports/ports-at-scale/noglyph
`*-standalone.html` + their PNGs) if you want to revisit the rejected options.

(Files under `.superpowers/` and `_preview/` are gitignored; the `docs/…/mockups-s2/` copies are the durable
record. Nothing was `jj commit`ed this session — do that if you want the v17 mock + this handoff committed.)

---

## 7. Fixed constraints the implementer MUST respect (unchanged across all handoffs)

- **File:** `src/Raun.Mtp/HtmlReport/report-template.html` (an `EmbeddedResource`); inline HTML/CSS/JS, model
  injected as JSON. The activity diagram **replaces** the current Gantt-timeline + object-flow SVG overlay
  (`.flow-svg`/`.conn`/`.dock`/`.flow-label`) the user found clunky.
- **Self-contained, HARD rule:** inline `<style>`/`<script>` only — **zero external URLs/CDNs/web-fonts/@import**.
  Source Serif 4 → **base64 woff2 embedded**. (The companion mocks link Google Fonts only because the companion
  isn't bound by this rule.)
- **JSON token:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`;
  `HtmlReportSink` string-replaces it. Don't break it.
- **Model field names are FIXED** (`src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`, camelCase serialized) — model &
  builder are NOT changing. Everything the renderer needs is already there:
  - `scenarios[].steps[]`: `stepId, index, label, phase` (Given/When/Then), `displayName, status, offsetMs,
    durationMs, lane, dependsOn[]` (**control flow / DAG edges**), `groupId, logs[],
    effects[]{verb,type,key,offsetMs,data}, exception, skipReason`.
  - `scenarios[].resources[]`: `type, key, events[]{verb,offsetMs,stepId}` (**object flow**; verbs
    create/read/edit/delete).
  - So: control flow = `dependsOn`; object flow = `resources`/`effects`; phase = `phase`; parallelism+timing =
    `offsetMs`/`durationMs`; forks = steps sharing `dependsOn`/`groupId` that overlap on different `lane`s.
- **Both themes:** auto light/dark + `?theme=light|dark`. The mocks are **dark-only** — the spec MUST define the
  **light** palette for the new diagram too.
- **0-warning build. Keep tests green:** `test/Raun.Mtp.Test/HtmlReportSinkTests.cs` (substring asserts — may
  need updating for new markup) and `HtmlReportModelBuilderTests.cs` (**model snapshot — must NOT change**).
- **Rendering-tech choice (flag in the spec):** the mocks are pure SVG. **Content-sized boxes** need a
  text-measure pass (`getBBox`/char-width estimate); **line-label placement** (§3) likewise needs measured text +
  geometry; the **cropped/centred inline timeline** and the **clamp+stack** port layout are computed from the
  model. Decide pure-SVG vs HTML+SVG-overlay in the spec.
- **VCS: `jj` only**, never git mutations (colocated repo; read-only `git log/diff/status` is fine).

---

## 8. Immediate next steps

1. **LINE LABELS (§3)** — brainstorm + iterate in the companion (placement rule, orientation, halo/legibility,
   crowded case); headless-verify each; **lock with the user.**
2. **Collapse tiers (§5)** — mock the `PATIENT ×12` stacked type-card and the full bundle conduit; lock.
3. **Write the spec** → `docs/superpowers/specs/2026-06-20-report-activity-diagram-design.md`: full visual spec
   (§2 + the §4 locked rules incl. gap-only crossings, uniform wire, arrowhead-to-port-centre), **both light +
   dark palettes**, the line-label model, the collapse model + thresholds, the rendering-tech choice and how
   content-sizing / cropped-centred timeline / clamp+stack / label-placement are computed from the model,
   base64-woff2 font embedding, and the test impact.
4. Self-review the spec → user review → `writing-plans` → implement (**TDD**; keep the model snapshot + sink
   substring tests green; preserve the JSON token + the self-contained rule).

(Naming thread from session 1 — rename **Raun**, candidates **Junction / Tracery / Cascade** — remains a
separate, untouched thread.)
