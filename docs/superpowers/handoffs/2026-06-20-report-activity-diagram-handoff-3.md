# Handoff 3 — activity-diagram visualization: cell-packaged fork, passthrough ports, converged

Date: 2026-06-20 (session 3)
Status: **core visual converged at `v14`. Four small tweaks remain before the visual is final, then: mock the
collapse tiers → write the spec → plan → implement (TDD).** Most big decisions are now LOCKED (see §4).
Continues: `2026-06-20-report-activity-diagram-handoff-2.md` (session 2) and `…-handoff.md` (session 1). Read this
one first; reach back to handoff-2 for the fork-timeline "special sauce" rationale and the full mockup history.

How to start the next session: read this file, open the latest mockup
`docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/v14-split-ring-passthrough.html` in a
browser, apply the §3 tweaks in the visual companion (verifying each with a headless render — see §6), then
proceed to §5/§8.

---

## 1. What we did this session

Iterated the per-scenario activity diagram from `v8` (handoff-2's best) through **`v14`** in the brainstorming
visual companion. The look has **converged**. Major moves this session:

- **Data ports** chosen: ring = input, **disc + faint halo = output** (candidate "A" from a 4-way mockup;
  diamonds/sockets/tabs/chevrons rejected). See `port-glyphs-candidates.html`.
- **Content-sized action boxes** (hug label + padding, min-width floor) replacing fixed-width rectangles.
- **The fork became a packaged "cell"** — the biggest evolution. A 4-walled contained component holding the
  inline timeline; objects leave through **ports in the wall**; a **thin "bond wire" inside thickens to the
  full object-flow line as it exits**.
- **Cell-wall passthrough port** went through `bond-wire → ]|[ bracket → colored funnel → ring → split-ring )(`,
  landing on **`)(` for now** (→ to become more `][`, see §3.2).
- **Connector grammar unified**: every connector = a curved **S-line + a label + a color**. Decision branches are
  splines labelled `Yes`/`No` (brackets dropped).
- **Layout rules introduced**: control flow **owns the center** of each node edge; data ports **clamp to a fixed
  inset from the edge and stack inward** (same inset for in & out so they align); **straight-down** routing where
  possible, **side-exit + down-loop** for divergent flows; **perpendicular** wall crossings; **input arrows point
  TO the ring** (tip just outside), not into it.
- Locked: **serif = Source Serif 4** (briefly switched to Newsreader, then reverted), **architecture confirmed**,
  **collapse deferred** with an agreed model (§5).
- Tooling: set up a **headless render loop** — write the mock to a file outside the watched dir, screenshot it
  with `npx playwright screenshot --browser=chromium`, eyeball it, then copy into the companion's `content/`.
  This catches SVG geometry bugs before the user sees them. Keep using it.

---

## 2. The converged design = `v14` (this is THE look — describe it in the spec)

A **flat, dark, top-down activity diagram**, per scenario. Near-square corners (`rx 1–3`), thin strokes, fine
serif, emphasis on hover/active only. Deliberately bends UML where it buys clarity.

### Frame & control flow
- **Horizontal Given/When/Then swimlane bands**, full width, flat tint ~`.06–.07`, each with a rotated phase
  label + a thin (`2.5px`) colored tab on its left edge. Hues: Given `#3f82e6`, When `#9a6ae0`, Then `#1aa48d`.
  **Band heights are content-driven** — Given is given extra height so the object cards sit clear below the fork
  (not crammed against it).
- **Control-flow spine runs straight down the centre axis** (hairline grey `#5c6571`, `1px`): **initial** node
  (filled light circle) → action nodes → **decision** diamond → **merge** diamond → **final** node (ring + core).
  **Control flow owns the CENTER of every node edge** and meets the fork/join sync bars directly (no passthrough
  glyph on control).
- **Action nodes** are **content-sized** boxes (fit label + horizontal padding, with a min-width floor for very
  short names), near-square (`rx 3`), phase-tinted fill (`#221a35` When, `#122a24` Then in the dark mock).
- **Decision/merge** = dark diamonds, thin stroke `#544470`, tiny centred label (e.g. `Slot free?`). Branch
  connectors are **splines** carrying a **plain label** `Yes`/`No` (no `[ ]` brackets).

### The fork = a packaged "cell" containing the inline timeline (the special sauce)
- A **4-walled contained component**: heavy slate **fork bar** (top) + **join bar** (bottom) carrying ruler tick
  notches, plus lighter **left + right walls** — together a closed cell (`#5f6873` walls over a `#161b22` panel).
- Inside: **the real overview-timeline machinery** (`report-template.html`'s `.timeline`/`.tl-row`/`.bar` with
  `niceAxis`/`fmtTick` ruler + `ms` gutter, faint gridlines, phase-hued lane bars carrying the white `G`/`W`/`T`
  glyph chip + serif label). One lane per parallel step by `offsetMs`/`durationMs`. **Cropped** to the fork's
  `max(offset+duration)` and **centered** under the spine; internal left/right dead space trimmed to hug the
  ruler; tight row pitch.
- **Each lane (the "die") has a disc port** at the end where its object is produced.
- **Objects leave the cell through a wall**: **straight down** through the join (bottom) wall for objects
  consumed below; **out a side wall + a down-loop** for divergent objects (the demo's `Database` exits the
  **left** wall and runs down the left margin to a Then assertion).
- **Passthrough port** where a data line crosses a wall: currently a **split-ring `)(`** glyph colored to the
  line, the line threading through the middle, crossing **perpendicular** (→ §3.2 wants it more `][` + a straight
  crossing). **Data flow only.**
- **The bond wire is THIN inside the cell** (disc → passthrough) and **thickens to full weight as it exits**,
  becoming the object-flow line. (Stepped thin→thick, the passthrough hides the junction. Subtle at real scale.)

### Object flow = entity cards + action-labeled edges
- **Each object = a card**: a **colored type-header band over a dark identifier body** (e.g. `PATIENT` over
  `Jane`; `#131922` body, thin `#2a313c` border, near-square `rx 1`).
- Flow runs **through** the card: `producer disc → [passthrough if it crosses a fork wall] → object card →
  consumer`.
- **Edge label = the action verb** (`create`/`read`/`edit`/`delete`), italic serif, in the object's color.
- **Edges are S-curves (ogee)** that leave and arrive perpendicular (→ §3.4 relaxes arrival to off-axis).
- **Ports**: input = hollow **ring**; output = filled **disc + faint halo**; colored per object. **Input arrows
  point TO the ring** (tip just outside it), not inside.
- **Data ports clamp to a fixed inset from the node edge and stack inward**; control owns center. **Same inset for
  inputs and outputs**, so an output (e.g. Appointment out of `CreateAppointment`) lines up directly above the
  matching input (Appointment into `AppointmentExists`). **When a side runs out of room → collapse (§5).**
- **Divergent object flow** (Given→Then around When) is supported.

### Style / colors (dark mock; spec must also define the LIGHT palette)
Object colors brightened for dark and **illustrative only** — the real renderer assigns per-type colors from the
existing `PALETTE`/`typeColor` machinery: Patient `#e08544`, Slot `#5cb877`, Appointment `#e06aa0`, Database
`#7c97f0`; slate walls `#5f6873`; control grey `#5c6571`.

---

## 3. OPEN tweaks to apply FIRST (before final lock) — all small deltas to `v14`

1. **Serif = Source Serif 4** (FINAL). The user switched to Newsreader briefly then **reverted**. `v14` is back on
   Source Serif 4. Embed as **base64 woff2** (self-contained rule). Fraunces/Newsreader/Spectral are out.
2. **Passthrough glyph: more `][` than `)(`.** The `)(` round split-ring should become squarer **`][`** — the
   bracket lips **wrap around the (grey) wall line a bit more**, and **the pass-through segment of the data line
   should be STRAIGHT** (a short straight perpendicular piece through the gap), **not a continuous curve** as it
   is now. Net: square brackets hugging the crossing + a straight crossing stub, then resume the S-curve outside.
3. **Arrowheads colinear with the line's end.** Each arrowhead must align with the **tangent at the line's end**
   (so it never looks "bent" relative to its edge), especially once arrivals go off-axis (#4). (SVG `orient=auto`
   gives this; verify it holds for every head.)
4. **Relax the perpendicularity requirement** for incoming/outgoing connections: allow **off-axis up to ~30° each
   side** of the edge normal (user said "30% each side"; this revises **down** from the earlier "45° each side /
   90° cone"). Lets edges arrive/depart at a natural angle instead of being forced normal; keep within the cone so
   ports still read cleanly.

(After these four, the visual is **final** — then §5, then the spec.)

---

## 4. LOCKED decisions

- **Serif:** Source Serif 4 (base64 woff2, embedded). [pending the §3.1 revert being carried into the build]
- **Architecture / scope:** the **activity diagram is the single per-scenario headline view**; the existing
  **drill panel stays** for per-step detail; the standalone **overview timeline is reused only inside forks**.
  **No** separate full-scenario timeline. (Confirmed by the user.)
- **Collapse:** deferred to next session as a mock, but the **model is agreed** (§5).
- **Data port glyphs:** ring = input, disc+halo = output. **Passthrough** = wall-crossing port (→ `][`).
- **Connector grammar:** every connector = an S-line + a label + a color; decision labels are `Yes`/`No`.
- **Layout rules:** control owns center; data clamps to a fixed edge inset and stacks (same inset in/out);
  straight-down or side-exit routing to minimize overlap; perpendicular wall crossings; input arrows point to the
  ring; bond wire thin inside the cell → thick outside.

---

## 5. Collapse tiers — agreed model (MOCK THIS NEXT SESSION, then spec it)

Density is decided **per producer→consumer edge bundle** (the set of objects one step hands to another), in
**three automatic tiers** with **click-to-expand**:

1. **Expanded** (bundle ≤ ~4 objects): individual entity cards, exactly as `v14`.
2. **Grouped by type** (exceeds the card threshold): **one card per type with a count**, drawn as a small
   **stack** (offset cards behind to signal multiplicity) — e.g. `PATIENT ×12` — one edge per type carrying the
   aggregate verb. So 20 mixed objects → ~3–4 type-cards instead of 20.
3. **Fully collapsed** (too many *types*, or counts still too large): a single **bundle conduit** between the two
   steps — one thicker edge + a neutral count chip (e.g. `23 objects`); the per-type breakdown moves to
   hover / the drill panel.

Auto-selected by thresholds (defaults to tune: `>4 objects → group by type`; `>4 type-groups or >~12 total →
full bundle`). Any bundle can be **clicked to expand one tier** (bundle → by-type → individual); the drill panel
always lists the full set. **The cell-wall passthrough is the natural home for collapse** — a single passthrough
port can represent `PATIENT ×12` and fan out (or stay bundled) below the wall.

---

## 6. Mockups (visual record) + companion

**Committed** to `docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/` (open in a browser;
they link Google Fonts only because the companion isn't bound by the self-contained rule). Session-3 evolution:
- `port-glyphs-candidates.html` — the 4-way data-port pick (ring/disc chosen).
- `v9-ports-and-content-boxes.html` — ring/disc ports + content-sized boxes.
- `v10-chip-packaged-fork.html` — fork as a packaged chip: bond wires → boundary ports.
- `v11-straight-routing-wall-ports.html` — straight-down routing, `]|[` wall ports, control-center/data-clamp.
- `v12-cell-wall-pores.html` — 4-walled cell, colored funnel pores on all sides, perpendicular crossings,
  S-curves, aligned in/out.
- `v13-passthrough-rings.html` — pore unified to a **ring**; bond wire thin-inside → thick-outside.
- `v14-split-ring-passthrough.html` — **current best**; pore is a split-ring `)(`, read-arrows point to the ring.

**Companion** (brainstorming visual companion): session dir `.superpowers/brainstorm/9542-1781954515/`. If the
server is down, restart with the **same `--project-dir`** (reuses the port; the user's tab auto-reconnects):
```
bash "/c/Users/redoz/.claude/plugins/cache/claude-plugins-official/superpowers/6.0.0/skills/brainstorming/scripts/start-server.sh" --project-dir "/c/dev/raun" --open
```
(Windows: run in background; then read `.superpowers/brainstorm/9542-1781954515/state/server-info` for the URL.)
Push a screen by writing the HTML into `…/content/` (server serves the newest file). The companion serifs are
swapped live via a picker setting `--dfont` on `#diagram`.

**Headless self-verify loop (use it):** write the mock to `…/_preview/` (OUTSIDE `content/` so it doesn't push to
the user), wrap it in a dark page, then:
```
npx playwright screenshot --browser=chromium --full-page --viewport-size=900,1500 _preview/vNN-standalone.html vNN.png
```
Inspect the PNG, fix geometry, then `cp` the verified file into `content/`. (Chromium is installed; system Chrome
needs admin and is not available. `--clip` is unsupported by the CLI; render full-page.)

---

## 7. Fixed constraints the implementer MUST respect (unchanged across all handoffs)

- **File:** `src/Raun.Mtp/HtmlReport/report-template.html` (an `EmbeddedResource`); inline HTML/CSS/JS, model
  injected as JSON. The activity diagram **replaces** the current Gantt-timeline + object-flow SVG **overlay**
  (`.flow-svg`/`.conn`/`.dock`/`.flow-label`) the user found clunky.
- **Self-contained, HARD rule:** inline `<style>`/`<script>` only — **zero external URLs/CDNs/web-fonts/@import**.
  Chosen serif (**Source Serif 4**) → **base64 woff2 embedded**.
- **JSON token:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`;
  `HtmlReportSink` string-replaces that token. Don't break it.
- **Model field names are FIXED** (`src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`, camelCase serialized) — model &
  builder are NOT changing. The renderer already has everything:
  - `scenarios[].steps[]`: `stepId, index, label, phase` (Given/When/Then), `displayName, status, offsetMs,
    durationMs, lane, dependsOn[]` (**control flow / DAG edges**), `groupId, logs[],
    effects[]{verb,type,key,offsetMs,data}, exception, skipReason`.
  - `scenarios[].resources[]`: `type, key, events[]{verb,offsetMs,stepId}` (**object flow**; verbs
    create/read/edit/delete).
  - So: control flow = `dependsOn`; object flow = `resources`/`effects`; phase = `phase`; parallelism+timing =
    `offsetMs`/`durationMs`; forks = steps sharing `dependsOn`/`groupId` that overlap on different `lane`s.
- **Both themes:** auto light/dark + `?theme=light|dark`. The mockups are **dark-only** — the spec MUST define the
  **light** palette for the new diagram too.
- **0-warning build. Keep tests green:** `test/Raun.Mtp.Test/HtmlReportSinkTests.cs` (substring asserts — may
  need updating for new markup) and `HtmlReportModelBuilderTests.cs` (**model snapshot — must NOT change**).
- **Rendering-tech choice (flag in the spec):** the mockups are pure SVG. **Content-sized boxes** need a
  text-measure pass (`getBBox`) or a char-width estimate; the **cropped/centered inline timeline** and the
  **clamp+stack** port layout are computed from the model. Decide pure-SVG vs HTML+SVG-overlay in the spec.

---

## 8. Immediate next steps

1. In the companion, apply the four §3 tweaks to `v14` (serif already reverted; do the `][` passthrough + straight
   crossing, colinear arrowheads, ±30° relaxed arrivals). **Headless-render each before pushing** (§6). Get the
   user to lock the final visual.
2. **Mock the collapse tiers** (§5): the `PATIENT ×12` stacked group-card and the full bundle conduit. Lock with
   the user.
3. Write the formal spec → `docs/superpowers/specs/2026-06-20-report-activity-diagram-design.md`: full visual spec
   (§2 + tweaks), **both light + dark palettes**, the collapse model + thresholds, the rendering-tech choice and
   how content-sizing / cropped-centered timeline / clamp+stack are computed from the model, base64-woff2 font
   embedding, and the test impact.
4. Self-review the spec → user review → `writing-plans` → implement (**TDD**; keep the model snapshot + sink
   substring tests green; preserve the JSON token + the self-contained rule).

(Naming thread from session 1 — rename **Raun**, candidates **Junction / Tracery / Cascade** — remains a
separate, untouched thread.)
