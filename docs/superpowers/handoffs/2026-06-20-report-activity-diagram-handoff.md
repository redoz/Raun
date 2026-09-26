# Handoff — modern activity-diagram visualization for the HTML report

Date: 2026-06-20
Status: **brainstorm converged on a design; not yet written as a formal spec or implemented.**
Origin: superpowers `brainstorming` skill + visual companion. This handoff is the design record so a fresh
session can finalize the spec (`docs/superpowers/specs/`) → `writing-plans` → implement.

---

## 1. What we're doing & why

The current per-scenario visualization in `src/Raun.Mtp/HtmlReport/report-template.html` is a Gantt timeline
with an **object-flow SVG overlay** (connectors docking resource lifelines into bars, identity chips, hover/pin
highlighting). The user finds the overlay **clunky**. Goal: replace it with a clean, modern **UML activity
diagram** rendered per scenario that shows **control flow + object flow** — styled so it doesn't look like a
dated Rational-Rose export.

Key realization during brainstorming: the activity diagram and the timeline **converge**. See §3.

---

## 2. Fixed context the implementer must respect

- **File:** `src/Raun.Mtp/HtmlReport/report-template.html` (an `EmbeddedResource`). Rendering is plain
  inline HTML/CSS/JS; the model is injected as JSON.
- **Self-contained, hard rule:** inline `<style>`/`<script>` only. **Zero external URLs/CDNs/web-fonts/@import.**
  → the chosen distinctive font (see §4) MUST be embedded as **base64 woff2**, not linked. (The mockups link
  Google Fonts only because the companion isn't bound by this rule.)
- **JSON-injection contract:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`;
  `HtmlReportSink` string-replaces that token. Don't break it.
- **Model field names are fixed** (`HtmlReportModel.cs`, camelCase serialized). The renderer already has
  everything needed:
  - `scenarios[].steps[]`: `stepId, index, label, phase` (Given/When/Then), `displayName, status,
    offsetMs, durationMs, lane, dependsOn[]` (← **control flow / DAG edges**), `groupId, logs[],
    effects[]{verb,type,key,offsetMs,data}, exception, skipReason`.
  - `scenarios[].resources[]`: `type, key, events[]{verb,offsetMs,stepId}` (← **object flow**;
    verbs create/read/edit/delete).
  - So: control flow = `dependsOn`; object flow = `resources`/`effects`; phase = `phase`; parallelism +
    timing = `offsetMs`/`durationMs` (now realistic thanks to the simulated-clock work, see the
    `2026-06-19-report-restyle-and-simulated-time` spec).
- Auto light/dark + `?theme=light|dark` override; 0-warning build; tests in
  `test/Raun.Mtp.Test/HtmlReportSinkTests.cs` (substring asserts) and `HtmlReportModelBuilderTests.cs`
  (model snapshot — must stay green; the model/builder are NOT being changed).

---

## 3. The agreed design (what to build)

**Per-scenario view = a top-down UML activity diagram**, with the full vocabulary:
initial node → action/step nodes → **decision/merge diamonds** with `[Yes]/[No]` (alternative/extension flows)
→ **fork/join** + parallel activities → activity-final node.
(Reference the user supplied: `C:\Users\redoz\Downloads\02-basic-activity-diagram.webp`. **NOTE:** the red
arrow callouts in that image are a *legend of element types we must support* — they are **NOT** rendered in the
output.)

### Layout & structure
- **Horizontal Given/When/Then swimlanes.** Vertical position encodes phase; flow runs top-down through the
  bands. Rotated phase label + a colored phase tab on the left edge of each band.
- Sequential steps = flat **activity nodes** (sharp-ish rectangles), step name **inside** the node.
- Decisions render as flat diamonds with small `[Yes]/[No]` labels; merge as a smaller diamond.

### The "special sauce" — a fork renders an **inline timeline**
The horizontal time axis appears **only inside a fork** (the one place relative timing/concurrency matters):
- The **fork & join bars are the heavy slate top/bottom border** of a contained timeline **component** (a
  "unit"). Chosen delineation: **filled (white) background panel** sitting on the swimlane tint (option A),
  *not* a full four-side border (option B was the alternative).
- The component shows **timing marks**: ruler ticks + labels (`0/100/200/300/400ms`), faint vertical
  gridlines, and tick notches on both bars — so it's unmistakably a timeline.
- Parallel steps = **lanes** (explicit zebra tracks + hairline separators), each a bar with the **name inside**
  and a colored fill = duration, all starting together so overlap is obvious.
- **LATEST DIRECTION (not yet mocked):** instead of the bespoke Gantt in the mockups, **reuse the existing
  overview-page timeline rendering** (the `.timeline`/`.tl-row`/`.bar` machinery already in
  `report-template.html` — `niceAxis`/`fmtTick` ruler, lane bars) and render it **inline** inside the fork
  unit, scoped to just that fork's parallel steps. The user thinks that original timeline looks nicer than the
  mockup's version.

### Object flow
- **Organic (curved) arrows**, colored per object/resource type, connecting **ports** on the steps.
- **Ports** = small **diamond** glyphs with pizazz (not plain squares): **hollow diamond = input** (top edge),
  **filled diamond with white core = output** (bottom edge), colored per object. (Top = input, bottom =
  output, matching the top-down flow.)
- Object identity (`Type:Key`) shown as a small label on/near the arrow.
- Object flow can **diverge** from control flow (e.g. a Database object set up in a Given step and asserted in
  a Then step, skipping When) — the arrow routes around. This must be supported (it's why we went to
  first-class object-flow edges rather than chips-on-the-edge).

### Visual style (firm)
- **FLAT.** No gradients, no default shadows/glows/outlines. (An early "luminous glass/neon" dark variant was
  explicitly rejected as "TRON/dated.")
- **Emphasis = hover/active ONLY.** At rest everything is flat; on hover a node/lane gets outline + lift, and
  its object/control-flow connectors brighten (connectors sit quiet/faint at rest).
- **Few fillets** — small corner radius, near-square cards/bars.
- **Distinctive serif font, semibold** (see §4).
- GWT palette: Given `#2f74d0`, When `#8350c4`, Then `#0c8576`. Object colors used in mockups: Patient
  `#e8590c`, Slot `#2f9e44`, Appointment `#e64980`, Database `#4263eb`; fork/join slate `#5b6675`.
- **LATEST DIRECTION (not yet mocked):** try a **dark theme**, and **scale everything down** (including
  font-size) — current proportions feel "Duplo," want "Lego" (finer/denser/more refined).
- Long step/scenario names: handled by name-in-bar/node with overflow + truncate-with-"…" + full-text on
  hover. (This is what killed the fixed-width centered-node approach.)

---

## 4. Open decisions (decide with the user, then write the spec)

1. **Font (serif, semibold) — NOT finalized.** Mockup used **Fraunces**; alternatives shown: **Newsreader**,
   **Source Serif 4**. Whatever is picked must be embedded base64 woff2.
2. **Dark theme + scale-down** — requested but not yet rendered. Next mockup should show the whole component
   dark and ~1 step smaller in scale/type.
3. **Use the original overview timeline inline in forks** — requested; needs a mockup wiring the existing
   `.timeline` rendering into the fork unit (scoped to the fork's steps).
4. **Architecture relationship:** is the activity diagram the *single* per-scenario view (with forks showing
   the inline timeline), or do we also keep a separate full-scenario timeline + the existing drill panel?
   Working assumption: activity diagram is the headline; keep the drill panel; the standalone overview timeline
   is reused *inside* forks. Confirm with user.
5. Minor: lane contrast, diamond/port size, bar-fill weight, exact corner radius, ruler density.

---

## 5. Mockups (the visual record)

All brainstorm mockups copied to **`docs/superpowers/handoffs/2026-06-20-report-mockups/`** (the live ones are
under gitignored `.superpowers/brainstorm/3812-1781945456/content/`). Open in a browser. Evolution order:
- `metaphor.html` — early (rejected) "objects as metro lines" exploration.
- `activity-topdown.html` — first real top-down activity diagram (inline-objects vs object-rail).
- `classic-modern.html` — control-flow + object-flow as **independent layers**, incl. divergent object flow.
- `ultramodern.html` — two "2025" aesthetics; the dark "luminous glass" one was **rejected (TRON/dated)**.
- `flat-swimlanes.html` — **flat**, GWT swimlanes, hover-only emphasis, font picker (was sans; user then asked
  for serif).
- `lane-timeline.html` — "one lane per activity = a timeline" (the user said this went **too far**).
- `fork-timeline.html` — the correction: **time axis only inside a fork**; fork/join as sync bars.
- `ports-and-fork.html` — fork = Gantt between the two bars, names in bars, **ports**, organic arrows, fewer
  fillets.
- `fork-unit.html` — fork as a contained "unit": A filled panel vs B full box (**A chosen**).
- `fork-refined.html` — **latest**: compact, zebra lanes, near-square bars, **diamond ports** (hollow=input /
  filled-core=output).

To restart the visual companion (same port, user's tab auto-reconnects):
`bash "$SUPERPOWERS/skills/brainstorming/scripts/start-server.sh" --project-dir "/c/dev/raun" --open`
(run in background on Windows). Session dir: `.superpowers/brainstorm/3812-1781945456/`.

---

## 6. Naming (separate thread)

User wants to rename **Raun**. Subagent shortlist (verify NuGet id + GitHub org + `.dev`/`.io` domain +
trademark before committing):
- **Top picks:** **Junction** (DAG node / fork-join / transit-map — strongest), **Tracery** (the traced
  flow-lines the report draws; note JS lib collision, different ecosystem), **Cascade** (dependency flow;
  leans sequential, common word).
- Runners-up: Slipstream, Strand, Skein, Plexus, Vela.
- **Avoid:** anything `…Unit` (looks derivative of TUnit/xUnit), **Saga** (.NET distributed-tx pattern),
  **Confluence** (Atlassian), Flux/Mesh/Stream (generic/taken).

---

## 7. Immediate next steps

1. Mock the **dark + scaled-down** version with the **original overview timeline rendered inline in the fork**
   (open decisions #2 + #3); confirm font (#1) and architecture (#4) with the user.
2. Write the formal design spec to `docs/superpowers/specs/2026-06-20-report-activity-diagram-design.md`.
3. Self-review the spec → user review → `writing-plans` → implement (TDD; keep the model snapshot + sink
   substring tests green; embed font base64; preserve the JSON token + self-contained rule).
