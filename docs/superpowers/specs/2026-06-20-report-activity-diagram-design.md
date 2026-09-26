# Report activity-diagram — design spec

Date: 2026-06-20
Status: **Design locked** (visual `v17` + line-labels + collapse tiers, all locked with the user over sessions
1–4). Ready for `writing-plans` → TDD implementation.
Supersedes the per-scenario visualization of `2026-06-07-html-report-design.md` /
`2026-06-19-report-restyle-and-simulated-time-design.md` (the Gantt-timeline + object-flow SVG overlay). Continues
the handoffs `docs/superpowers/handoffs/2026-06-20-report-activity-diagram-handoff{,-2,-3,-4}.md`.

**Visual source of truth (open in a browser):**
- Locked diagram: `docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/v17-gap-crossings.html`
- Line-label rounds (companion fragments): `.superpowers/brainstorm/9542-1781954515/content/labels-r1.html`,
  `labels-r2.html`, `labels-r3.html` (final settings) — and `collapse-r2.html` (collapse tiers).
  Gitignored working copies + headless PNGs live under `.superpowers/brainstorm/9542-1781954515/_preview/`.

---

## 1. Goal & scope

Replace the clunky per-scenario **Gantt-timeline + object-flow SVG overlay** with a single, flat, **top-down UML-ish
activity diagram** per scenario — the headline view of what a scenario did: its control flow (DAG), its parallelism
(forks), and its object flow (resources created/read/edited/deleted). Both **light and dark** themes. The existing
**drill panel stays** for per-step detail; the overview **timeline is reused only inside forks**. No separate
full-scenario timeline.

### Affected file
`src/Raun.Mtp/HtmlReport/report-template.html` — an `EmbeddedResource`; all HTML/CSS/JS inline, model injected as
JSON. Rendered by `HtmlReportSink` (string-replaces the JSON token). **No C# model/builder changes.**

### REUSE / REPLACE / PRESERVE (current template, ~869 lines)

| Bucket | What | Identifiers (approx. lines) |
|---|---|---|
| **PRESERVE** | Document shell, `<title>Raun run report</title>`, the JSON token, app bar/brand, summary `id="chips"`, gen line, phase legend | head ~3–7; `#chips` ~338; token ~353 |
| **PRESERVE** | The model parser | `const model = JSON.parse(document.getElementById("model").textContent)` ~364 |
| **PRESERVE** | Drill panel + focus | `.drill`/`.step-list`/`.step-entry` ~235–289; `buildScenarioDrill(sc)` ~824–866; `focusStep(stepId)` ~717 |
| **REUSE** | Theme system | CSS vars on `:root`, `@media (prefers-color-scheme)`, `[data-theme]` overrides ~10–85; `?theme` parse ~358–361 |
| **REUSE** | Object colors | `PALETTE` ~417 + `typeColorMap` + `typeColor(t)` ~422–426 |
| **REUSE (math only, re-rendered as SVG)** | Time axis + bar geometry | `niceAxis(maxMs)` ~379; `fmtTick(ms,axisMax)` ~388; bar math `x=offsetMs*px`, `w=max(minBar,durationMs*px)`, `px=track/axisMax` ~662,742; `MIN_BAR` ~363 |
| **REUSE** | Formatters | `fmtDur()`, `fmtGen()` ~369–376 |
| **REPLACE / REMOVE** | Per-scenario Gantt bars + lanes | ~700–758 |
| **REPLACE / REMOVE** | Resource rows, lifelines, markers | `.res-row`/`.lifeline`/`.marker` ~318–327, ~762–808 |
| **REPLACE / REMOVE** | Object-flow SVG overlay | `.flow-svg`/`.conn`/`.dock`/`.flow-label` ~290–327; `buildFlowOverlay(ctx)` ~524–640 |
| **REPLACE / REMOVE** | Hover/pin highlight machinery + flow state + flow legend | `clearLit`/`applyLit`/`litByStep`/`litByRes`/`refresh` ~488–521; `flows`/`flowCtx`/`pinned` ~449–453; `.flow-legend` ~322–326, ~429–439 |

`renderScenario(sc)` is rewritten to produce the SVG activity diagram (plus the unchanged drill). The new
per-scenario interaction (hover emphasis, click-to-expand collapse, click-a-node → `focusStep`) replaces the old
flow-highlight machinery.

---

## 2. The locked visual (`v17`)

A flat, dark/light, top-down activity diagram per scenario. Near-square corners (`rx 1–3`), thin strokes, fine
serif; emphasis on hover/active only. Bends UML where it buys clarity.

### 2.1 Frame & swimlanes
- **Horizontal Given/When/Then bands**, full width, flat tint (~`.06–.07` dark / ~`.08–.10` light), each with a
  rotated phase label + a thin (`2.5px`) colored tab. Hues = existing `--ph-given` / `--ph-when` / `--ph-then`
  (mock: Given `#3f82e6`, When `#9a6ae0`, Then `#1aa48d`).
- **Band heights are content-driven** (Given gets extra height so object cards clear the fork).

### 2.2 Control flow (the spine)
- **Straight hairline spine down the centre axis** (grey `#5c6571`, `1px`): initial node (filled circle) → action
  nodes → decision diamond → merge diamond → final node (ring + core). **Control owns the CENTRE of every node
  edge** and meets fork/join bars directly. Source = `dependsOn` (the DAG).
- **Action nodes** = content-sized boxes (label + padding, min-width floor), near-square (`rx 3`), phase-tinted.
- **Decision/merge** = dark diamonds, thin stroke, tiny label; branch connectors are **splines** with a plain
  `Yes`/`No` label (no brackets). Curved branches end with a **short straight tail** so the arrowhead lands square
  (see §2.6).

### 2.3 The fork = a packaged "cell" with the inline timeline (special sauce)
- A **4-walled cell**: heavy slate **fork bar** (top) + **join bar** (bottom) with ruler ticks + lighter
  **left/right walls** (`#5f6873` walls over a `#161b22` panel dark / `#eef1f5` over walls `#aab2bd` light).
- Inside: the **real overview-timeline machinery**, re-rendered as SVG (see §6) — `niceAxis`/`fmtTick` ruler + `ms`
  gutter, gridlines, phase-hued lane bars carrying a white `G/W/T` chip + serif label. One lane per parallel step
  by `offsetMs`/`durationMs`. **Cropped** to the fork's `max(offsetMs+durationMs)` and **centred** under the spine.
- **Each lane has a disc port** at its production end.

### 2.4 Wall crossing = a clean gap (NO glyph)
- Where a data line crosses a wall, the wall simply has a **clean gap and the line passes straight through it** —
  the gap *is* the port. Continuous, **perpendicular**, full object-flow weight. No grommet / no `][` / no
  circle-dot (that entire subsystem was explored and **deliberately dropped**). Mock: join-wall gaps ≈ 4px (line
  ~1.3 with ~1.3 clearance each side); the left-wall (database) gap is the same idea rotated 90°.

### 2.5 Object flow = entity cards + action-labelled edges
- **Each object = a card**: a colored **type-header band over a dark/white identifier body** (e.g. `PATIENT` over
  `Jane`). Body `#131922` dark / `#ffffff` light; border `#2a313c` / `#d0d7de`; `rx 1`.
- Flow runs **through** the card: `producer disc → [wall gap if it crosses a wall] → object card → consumer`.
- **Edges are S-curves**; leave/arrive relaxed to **off-axis up to ~30°**.
- **Object-flow line is ONE uniform width** (`1.3` in mock) the whole way — no thin/thick (the old thin→thick bond
  wire is retired).
- **Divergent objects exit a side wall + down-loop** (mock: `Database` exits the left wall, runs down the left
  margin to a Then assertion).

### 2.6 Ports & arrowheads
- **Ports**: input = hollow **ring**; output = filled **disc + faint halo**; colored per object.
- **Data ports clamp to a fixed inset from the node edge and stack inward**; control owns centre; **same inset for
  in & out** so an output lines up above the matching input. When a side runs out of room → collapse (§4).
- **Arrowheads**: the **base sits centered on the line** (curved arrivals end with a short straight tail so it
  lands square, not skewed off the tangent); arrivals at a node port aim their **centreline at the ring/port
  centre**; **tip just outside** the port.

---

## 3. Line (edge) labels — LOCKED

The `create/read/edit/delete` verbs on object-flow edges. (Source: label rounds r1–r3; final settings r3.)

### 3.1 Type & color
- **Source Serif 4, italic, weight 500, size 5.5** (diagram units).
- **Color = the object's base color pushed toward the theme background**: brightened on dark, darkened on light
  (computed from the object's `typeColor` — see §5.3). The **word** carries the verb (create vs read), not the color.
- **Knockout halo**: SVG `paint-order: stroke` with `stroke = the local background color`, **stroke-width 2.4** —
  panel bg on dark (`#0d1117`), paper on light (`#ffffff`); cell bg where a label sits inside the fork cell. Keeps
  the verb crisp over a band tint, the cell, or open canvas, in both themes.

### 3.2 Placement ladder (deterministic from geometry; stop at the first step with room)
1. **On the line** — center the verb in a knockout gap at the visible mid-segment.
2. **Short wire** — if the segment is shorter than the word, keep it centered; the halo lets it overhang.
3. **Collision** — if the verb's box would hit a card, the cell, or a neighbour, slide along the wire, then step
   along the **normal** to the less-crowded side.
4. **Boxed in** — only if both sides are blocked, place it in the nearest clear space and draw a **dashed leader**
   (`stroke-dasharray ≈ 2.2 1.8`, in the object color, quiet) back to the wire. *Leader heuristic:* drawn only when
   the verb was displaced **past a threshold distance** from its wire **and** another label/port sits between the
   verb and the wire (so a bare floating word would be ambiguous). Dashed, because **solid = real object flow,
   dashed = mere pointer** — the leader must never read as a flow line.

Most edges stop at step 1. Density is handled by **collapse** (§4), not by hiding labels — labels are always-on.

---

## 4. Collapse tiers — LOCKED

Density is decided **per producer→consumer edge bundle**, with three automatic tiers and **click-to-expand**
(expand one tier; the drill panel always lists the full set). (Source: `collapse-r2`.)

### 4.1 The tiers
1. **Tier 1 · Expanded** — individual entity cards with individual colored edges, as §2.5. (≤ ~4 objects.)
2. **Tier 2 · Grouped by type** — **one card per type**, drawn as a small **stack** (two offset cards behind), with
   a **corner count badge** (object-color circle, e.g. `12`) and a **sample identifier** in the body (`Jane…`). The
   colored edge carries a **single uniform verb** (`create` / `read`). Used while each type-group's verb is uniform.
3. **Tier 3 · Bundle (grey)** — fired the moment a grouped edge **mixes verbs**, or types/counts explode. Rendered
   as a card in the **normal shape but greyed**: a **grey blank header band** (no text), blank body, and a **grey
   count badge** (total, e.g. `23`); a **grey stack** behind it hints at "many". Edges are **grey**. Each edge is
   labelled with **verb × count, one per occurring verb** (e.g. producer side `create ×23`; consumer side
   `read ×18`, `delete ×5`, stacked). No `⊕`/symbol — the whole card is the click-to-expand affordance. The grey
   header + grey badge mirror a normal card's anatomy so it reads as "a card, but mixed — expand me," and never
   competes with a live colored object flow.

### 4.2 Thresholds (defaults — tunable constants)
- `≤ 4 objects` in the bundle → **Tier 1**.
- `> 4 of a type` **and** that group's verb is uniform → **Tier 2** (one stack + badge per type).
- a group **mixes verbs**, OR `> 4 type-groups`, OR `> ~12 total` → **Tier 3** (grey bundle, verb×count).
- Any collapsed node **click-expands one tier**; the drill panel always has the complete list.

### 4.3 Which verb (resolves handoff Q6) & density (Q7)
- The **card carries the count**, the **edge carries the verb**. One verb almost always → the edge reads
  `create`/`read`. Mixed verbs never get faked onto a single colored edge — they drop straight to the grey bundle
  and are tallied as **verb × count** per occurring verb.
- **Always-on** labels at every tier; collapse (not hover-hiding) controls density. Full per-object breakdown is on
  click-to-expand or in the drill panel.

---

## 5. Palettes (both themes) — define as CSS custom properties

Add an activity-diagram-scoped set of CSS vars to the existing `:root` blocks (light default + dark `@media` +
`[data-theme]` overrides), alongside the existing `--ph-*` / `--v-*` vars. Reuse `--ph-given/when/then` for band
hues. Names below are proposed (`--ad-*` = activity-diagram).

### 5.1 Structural colors

| Role | var | Dark | Light |
|---|---|---|---|
| Diagram panel / canvas | `--ad-panel` | `#0d1117` | `#ffffff` |
| Fork cell bg | `--ad-cell` | `#161b22` | `#eef1f5` |
| Cell inner row / zebra | `--ad-cell-row` | `#1a212b` | `#e4e8ee` |
| Walls (fork/join/side) | `--ad-wall` | `#5f6873` | `#aab2bd` |
| Control spine / grey | `--ad-control` | `#5c6571` | `#8b929c` |
| Gridline | `--ad-grid` | `#1d2531` | `#d8dde4` |
| Card body | `--ad-card` | `#131922` | `#ffffff` |
| Card border | `--ad-card-border` | `#2a313c` | `#d0d7de` |
| Card identifier text | `--ad-card-ink` | `#e6edf3` | `#1f2328` |
| Action box (phase-tinted) | (derive from `--ph-*`) | e.g. When `#221a35` | e.g. When `#efeaf9` |
| Grey bundle header/badge | `--ad-grey` / `--ad-grey-badge` | `#5c6571` / `#7e8794` | `#aab2bd` / `#8b93a0` |
| Band tint opacity | `--ad-band-op` | `.06–.07` | `.08–.10` |

### 5.2 Object colors
Per-type object color (lines, ports, card headers, label base) comes from the **existing** `PALETTE` + `typeColor(t)`
(REUSE, §1). The same color is used in both themes (brand-consistent); contrast is handled by the label-derivation
(§5.3) and by the card body/halo, which flip with the theme.

### 5.3 Label color derivation
`labelColor(objColor, theme)` = the object color shifted toward the theme background: on **dark**, raise lightness
(~+12–15% L, e.g. mix ~22% toward white); on **light**, lower lightness (~−12–15% L, e.g. mix ~22% toward black).
Implement with a small JS `shade(color, amt)` helper that parses the `typeColor` output (hex **or** `hsl(...)`,
since hashed types return hsl) to RGB/HSL and lerps. (Do **not** rely on CSS `color-mix()` — keep it portable.)

---

## 6. Rendering technology

**Decision: pure SVG**, one `<svg>` per scenario, built in JS from `model.scenarios[i]` at load. Rationale: the
mocks are pure SVG; the fork cell visually *contains* the timeline, so one coordinate system is simplest; SVG gives
`getBBox()` / `getComputedTextLength()` for content-sizing and label placement. The existing HTML timeline DOM
(`.timeline`/`.tl-row`/`.bar`) is **not** reused; only its **math** (`niceAxis`, `fmtTick`, the `offsetMs*px` /
`max(minBar, durationMs*px)` geometry) is reused, re-emitting SVG `<rect>`s.

### 6.1 What is computed from the model
- **Layout** per scenario: order steps by `index`; group fork members (steps sharing `dependsOn`/`groupId` that
  overlap on different `lane`s); place initial/action/decision/merge/final on the spine; place object cards in the
  Given band; route object edges producer→card→consumer using `resources[]` / `effects[]`.
- **Control edges** = `dependsOn`. **Object flow** = `resources[].events[]{verb,offsetMs,stepId}` (and
  `steps[].effects[]` for per-step detail). **Phase** = `phase`. **Parallelism/timing** = `offsetMs`/`durationMs`.
- **Content-sized boxes**: measure label text (`getComputedTextLength`/`getBBox`) → box width = text + padding,
  clamped to a min-width floor.
- **Cropped/centred fork timeline**: `axisMax` from `niceAxis(max(offsetMs+durationMs) over fork lanes)`;
  `px = trackWidth/axisMax`; bars at `x = offsetMs*px`, `w = max(minBar, durationMs*px)`; the whole cell centred
  under the spine.
- **Clamp + stack ports**: data ports at a fixed inset from the node edge, stacking inward; same inset in/out so an
  output aligns above the matching input; overflow → collapse (§4).
- **Label placement**: the ladder (§3.2) run against measured label boxes vs. card/cell/edge/neighbour rectangles.

### 6.2 Font-load timing
Source Serif 4 must be loaded **before** measuring text (metrics differ from fallback). Gate the first render /
re-measure on `document.fonts.ready` (and/or `document.fonts.load("500 italic 12px 'Source Serif 4'")`). Until
loaded, render with the fallback then re-measure, or defer the first paint to `fonts.ready`.

### 6.3 Interaction
- Hover a card/edge → emphasize that object's full path (the only "lit" state; default is flat).
- Click a collapsed stack/bundle → expand one tier (re-layout that bundle).
- Click an action/assert node → `focusStep(stepId)` (existing drill behavior, PRESERVED).
- This replaces the removed `flows`/`pinned`/`applyLit` machinery.

---

## 7. Font embedding (self-contained, HARD rule)

- **Source Serif 4** is embedded as **base64 woff2** via an inline `@font-face` (the report links **zero** external
  URLs/CDNs/web-fonts/`@import`). The template is currently **system-fonts only** — this `@font-face` is new.
- Embed the **italic 500** and **roman 400/500/600** opsz instances actually used (labels are italic-500; node/card
  text is roman). Subset to Latin to control size; note the **file-size increase** of the embedded report (one woff2
  per needed instance, base64-inflated ~33%). Prefer the variable woff2 if a single file covers the needed weights
  and the italic axis; otherwise embed the minimal set.
- Set `--ad-font: 'Source Serif 4', Georgia, serif;` and use it for all diagram text. Monospace (`ui-monospace`)
  stays for ruler ticks / `ms` / code, as today.

---

## 8. Data mapping (model → diagram)

No model changes. Everything needed exists in `HtmlReportModel`:

| Diagram element | Model source |
|---|---|
| Control spine / DAG edges | `steps[].dependsOn[]`, `steps[].index` |
| Swimlane band | `steps[].phase` (Given/When/Then) |
| Action / assert node label | `steps[].displayName` (fallback `label`) |
| Node status styling | `steps[].status`, `steps[].exception`, `steps[].skipReason` |
| Fork membership + inline timeline | steps sharing `dependsOn`/`groupId` overlapping on different `lane`; `offsetMs`/`durationMs` |
| Object entity card | `resources[]{type,key}` (header = type, body = key); color via `typeColor(type)` |
| Object-flow edge + verb label | `resources[].events[]{verb,offsetMs,stepId}` (per-hop verb) |
| Collapse counts / grouping | grouping over `resources[]` by `type`; counts per type / per verb |
| Per-step detail (drill) | `steps[].effects[]{verb,type,key,offsetMs,data}`, `logs[]`, `exception`, `skipReason` (PRESERVED `buildScenarioDrill`) |

---

## 9. Test impact

- **`HtmlReportModelBuilderTests`** (incl. the `Verify(json)` snapshot): **must NOT change.** The C# model and
  builder are untouched. ✅ by construction.
- **`HtmlReportSinkTests`**: substring asserts on the rendered HTML. Must keep passing — preserve: the JSON token
  (`/*__RAUN_REPORT_JSON__*/`) and its replacement, the indented JSON (`"scenarioId": "scn"`), the scenario name
  text, `id="chips"`, the title text `Raun run report`, and `data-theme` wiring. New markup is fine as long as
  these substrings remain. Add/adjust asserts only if we want to lock new structure (optional).
- **Build**: **0 warnings**. Self-contained (no external URL) must hold — a CI/test check that the emitted HTML
  contains no `http://`/`https://`/`@import` for assets is desirable (the embedded font is base64, so this stays
  true).

---

## 10. Constraints recap

- Self-contained HTML: inline `<style>`/`<script>` only; Source Serif 4 base64-embedded; no CDN/web-font/`@import`.
- Exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`; don't break it.
- Model field names are FIXED (camelCase serialized); model + builder unchanged.
- Both themes: auto light/dark + `?theme=light|dark`; define the light palette (§5).
- Keep `HtmlReportSinkTests` + `HtmlReportModelBuilderTests` green; 0-warning build.
- VCS: **`jj` only** (colocated repo; read-only `git log/diff/status` fine).

## 11. Open implementation choices (decide in the plan, not blockers)

1. **Font packaging**: single variable woff2 vs. a minimal set of static instances (italic-500 + roman). Pick the
   smallest that covers §7; measure the resulting report size.
2. **`shade()` color helper**: exact lightness delta / mix ratio for label brighten/darken (§5.3) — tune to AA-ish
   contrast on both themes.
3. **Collapse thresholds** (§4.2): confirm the `4 / 4 / 12` defaults after seeing real scenarios; expose as
   constants.
4. **Label-placement de-overlap**: greedy single-pass (like the old `flow-label` nudge) vs. a small constraint
   pass; start greedy, escalate only if needed.
5. **Self-contained CI guard**: whether to add a test asserting "no external asset URLs in the emitted HTML."
