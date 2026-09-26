# Handoff 2 — activity-diagram visualization: dark, scaled, object entities

Date: 2026-06-20 (session 2)
Status: **design converged through 8 live mockups; ready for spec EXCEPT two small visual tweaks and two
decisions still open.** Do NOT write the spec until those are closed (see §3, §4).
Continues: `docs/superpowers/handoffs/2026-06-20-report-activity-diagram-handoff.md` (session 1 — read it for the
original brainstorm, the fork-timeline "special sauce" rationale, and the naming thread).

---

## 1. What we did this session

Picked up at session-1's "immediate next steps" and iterated the per-scenario activity diagram live in the
brainstorming **visual companion**, 8 versions (`v1`→`v8`, all saved — see §5). The look has **converged**. We
applied all of session-1's requested directions (dark theme, scale-down, real inline timeline) and then the user
drove several more refinements. Current best = **`smaller-type-v8.html`**.

---

## 2. The converged design (this is the look — describe it in the spec)

A **flat, dark, top-down UML-ish activity diagram**, rendered per scenario. Deliberately bends UML where it buys
clarity — goal is innovative, instantly-readable UX, not a standards-compliant export.

### Frame & control flow
- **Horizontal Given/When/Then swimlane bands** (full width). Flat phase tint ~`.06–.07`. Each band has a
  rotated phase label + a thin (`2.5px`) colored tab on its left edge.
- Phase hues (dark-mode values, from the report's own palette): Given `#3f82e6`, When `#9a6ae0`, Then `#1aa48d`.
- **Control-flow spine runs straight down the centre axis** (`dependsOn` = the edges). Hairline grey (`#5c6571`,
  `1px`). Vocabulary: **initial** node (filled light circle) → action nodes → **decision** diamond (with
  `[Yes]/[No]` on the outgoing edges) → **merge** diamond → **final** node (ring + core).
- Decision/merge = dark-filled diamonds, thin stroke `#544470`, tiny label (e.g. `Slot free?`).

### The fork = an inline Gantt timeline "unit" (the special sauce)
The time axis appears **only inside a fork** — the one place concurrency/timing matters.
- A **contained module**: filled panel (`#161b22`) whose **heavy slate top bar = the fork** and **bottom bar =
  the join** (`#5f6873`, `3px`), each with tick notches.
- Inside it: **the existing overview-timeline rendering** (`report-template.html`'s `.timeline`/`.tl-row`/`.bar`
  machinery — `niceAxis`/`fmtTick` ruler with the `ms` gutter, faint vertical gridlines, phase-hued lane bars
  carrying the white `G`/`W`/`T` glyph chip + white serif label). One lane per parallel step, positioned by
  `offsetMs`/`durationMs`, all starting together.
- **Cropped to its own content**: axis max = the fork's `max(offset+duration)` through `niceAxis` (the empty
  trailing region is gone), width tracks the widest parallel step **capped at ~75% (we used ~60%)**, and the unit
  is **centred under the control-flow spine** (spine enters the fork bar at its centre).
- **Tight** row pitch (~`18px`), thin bars (~`12px`) — reads as a dense Gantt strip, not an airy panel. Faint
  zebra row tracks behind the lanes.

### Object flow = UML object nodes + action-labeled edges
This was the biggest evolution this session (away from session-1's "ports + identity-chip-riding-the-arrow").
- **Each object is its own entity card**, and the flow runs **through** it:
  `producing step → (edge) → object card → (edge) → consuming step`.
- **Card = a colored type HEADER band over an identifier body** (final form, v7/v8): top strip filled with the
  **type** colour showing the **type/class** in a small font (e.g. `PATIENT`, ~`5.5px`, dark text on the colour);
  body below (dark `#131922`) showing the **identifier/instance** larger (e.g. `Jane`, ~`7.5px`, light text).
  Thin border `#2a313c`, near-square (`rx 1`). (Earlier tried: identity chip on the arrow → rejected; single-line
  `Type:Key` → ok; colored left-edge bar → replaced by the header band.)
- **The edge label is the ACTION verb** — `create / read / edit / delete` — italic serif, in the object's
  colour. (The object identity is NOT on the arrow anymore; it's in the card.)
- Edges: thin (`1.1px`), curved/organic, **colored per object type**, opacity ~`.85`.
- **Object flow may diverge from control flow** and must be supported: the demo shows a `Database` object
  *created in a Given step and read in a Then step, routing around the When band entirely*.
- **Data ports** where an edge meets a step: currently small **diamonds** — hollow = input (top edge), filled +
  core = output (bottom edge), colored per object. **← one of the open tweaks (§3.2).**

### Style (firm)
- **FLAT, dark, emphasis on hover/active only.** Thin strokes. **Near-square** corners everywhere (`rx 1–3`).
  Small/fine type. **Distinctive serif** (see §4.1). Object colours brightened for dark: Patient `#e08544`,
  Slot `#5cb877`, Appointment `#e06aa0`, Database `#7c97f0` (illustrative — the real renderer assigns per-type
  colours from the existing `PALETTE`/`typeColor` machinery; slate fork/join `#5f6873`).

---

## 3. OPEN visual tweaks (mock these first, before the spec)

1. **Action boxes should be content-sized.** Right now `CreateAppointment`/`AppointmentExists` are fixed-width
   rectangles; make them fit their label + padding (like the object cards do). If the diagram is rendered as
   SVG this needs a text-measure pass (`getBBox`) or a char-width estimate; if rendered as HTML it's natural.
   Flag the rendering-tech choice in the spec.
2. **Nicer data-port visual.** The little diamonds underwhelm — "we really need a nicer visual for the data
   ports." Keep the semantics (input on top / output on bottom, colored per object) but find a better glyph.
   Candidates to mock next: a **socket/notch** cut into the node edge; a small **plug/tab lozenge**; a
   **concentric ring (input) vs filled dot (output)**; a **chevron**. Pick one in the companion with the user.

---

## 4. Decisions still to confirm (then write the spec)

1. **Font (serif).** Leaning has **shifted to Source Serif 4** — Fraunces (session-1's lead) was dropped as
   **too heavy/high-contrast at small sizes**. v2–v8 ship a live picker (Fraunces / Newsreader / Source Serif 4 /
   Spectral); user left it on **Source Serif 4** through the last several rounds but hasn't said "lock it." Get an
   explicit pick. Whatever wins **MUST be embedded as base64 woff2** (self-contained rule), not linked — the
   mockups link Google Fonts only because the companion isn't bound by that rule.
2. **Architecture / scope of the per-scenario view.** Working assumption (confirm): the **activity diagram is the
   single per-scenario headline view**; the existing **drill panel stays** for per-step detail; the standalone
   overview timeline is **reused only inside forks**. Open question is whether to *also* keep a separate
   full-scenario timeline — assumed **no**. Confirm with the user.

---

## 5. Mockups (visual record)

Copied to **`docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/`** (live ones are under
gitignored `.superpowers/brainstorm/9542-1781954515/content/`). Open in a browser. Evolution:
- `dark-scaled-inline-timeline.html` (v1) — dark + scaled + real inline timeline in the fork, all at once.
- `thin-square-fonts-v2.html` — thinner strokes, near-square corners, **live serif picker** added.
- `tight-gantt-fork-v3.html` — fork whitespace squeezed out → tight Gantt strip (zebra + gridlines).
- `object-entities-v4.html` — **objects become entity nodes; arrow labels become the action verb**; lighter/smaller; default serif → Source Serif 4.
- `two-line-objects-v5.html` — object cards become two-line (type over identifier).
- `cropped-timeline-v6.html` — fork timeline cropped to content + width-capped (~60%).
- `centered-header-cards-v7.html` — fork **centred**; object card = **colored type header + identifier body** (left bar removed).
- `smaller-type-v8.html` — **current best**; v7 with all type dialed down ~1px.

Restart the companion (same project dir → same port, user's tab auto-reconnects). On Windows run in background:
`bash "/c/Users/redoz/.claude/plugins/cache/claude-plugins-official/superpowers/6.0.0/skills/brainstorming/scripts/start-server.sh" --project-dir "/c/dev/raun" --open`
then read `<session>/state/server-info` for the URL. Session dir: `.superpowers/brainstorm/9542-1781954515/`.
The pickers swap the diagram serif live via inline `onclick` setting `--dfont` on `#diagram`.

---

## 6. Fixed constraints the implementer MUST respect (unchanged from session 1)

- **File:** `src/Raun.Mtp/HtmlReport/report-template.html` (an `EmbeddedResource`); inline HTML/CSS/JS, model
  injected as JSON. This replaces the current Gantt-timeline + object-flow SVG **overlay** (the `.flow-svg`,
  `.conn`, `.dock`, `.flow-label` machinery) the user found clunky.
- **Self-contained, hard rule:** inline `<style>`/`<script>` only — **zero external URLs/CDNs/web-fonts/@import**.
  The chosen serif → **base64 woff2 embedded**.
- **JSON token:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`;
  `HtmlReportSink` string-replaces that token. Don't break it.
- **Model field names are fixed** (`src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`, camelCase serialized) — the
  model/builder are NOT changing. The renderer already has everything:
  - `scenarios[].steps[]`: `stepId, index, label, phase` (Given/When/Then), `displayName, status, offsetMs,
    durationMs, lane, dependsOn[]` (**control flow / DAG edges**), `groupId, logs[],
    effects[]{verb,type,key,offsetMs,data}, exception, skipReason`.
  - `scenarios[].resources[]`: `type, key, events[]{verb,offsetMs,stepId}` (**object flow**; verbs
    create/read/edit/delete).
  - So: control flow = `dependsOn`; object flow = `resources`/`effects`; phase = `phase`; parallelism+timing =
    `offsetMs`/`durationMs`. Forks = steps sharing `dependsOn`/`groupId` that overlap in time on different `lane`s.
- Auto light/dark + `?theme=light|dark` override; 0-warning build. **Keep green:**
  `test/Raun.Mtp.Test/HtmlReportSinkTests.cs` (substring asserts) and `HtmlReportModelBuilderTests.cs` (model
  snapshot). Substring asserts may need updating for new markup, but the model snapshot must NOT change.
- Note: the design is **dark-first** but the report supports both themes — the spec must define the **light**
  palette for the new diagram too (the mockups only showed dark).

---

## 7. Immediate next steps

1. In the companion: mock the **content-sized action boxes** (§3.1) and a **nicer data-port glyph** (§3.2);
   get the user to **lock the serif** (§4.1) and **confirm architecture/scope** (§4.2).
2. Write the formal spec → `docs/superpowers/specs/2026-06-20-report-activity-diagram-design.md`. Include: the
   full visual spec above, **both** light + dark palettes, the rendering-tech choice (pure SVG vs HTML+SVG
   overlay) and how content-sizing + the cropped/centred inline timeline are computed from the model, the
   base64-woff2 font embedding, and the test impact.
3. Self-review the spec → user review → `writing-plans` → implement (TDD; keep the model snapshot + sink
   substring tests green; preserve the JSON token + self-contained rule).

(Naming thread from session 1 — rename Raun, candidates Junction / Tracery / Cascade — remains a separate,
untouched thread.)
