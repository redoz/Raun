# Design — Report-wide sun-driven lighting (Phase 2, multi-SVG architecture)

> Status: **design, approved to spec** (2026-06-21). Phase 1 (fork graph view + per-fork toggle) shipped to
> `main` (`e9f30a4d`). This doc resolves the **multi-SVG** concerns the single-SVG mock left open, so the
> feature can be planned to concrete depth. Terminal step: user review → `writing-plans` →
> `subagent-driven-development`.

## 1. Summary

The Raun HTML report renders **one `<svg class="actdiag">` per scenario** (`buildActivityDiagram`), each with
its own `viewBox`/coordinate space and its own `<defs>`. Phase 2 adds a report-wide **single-light "sun"
lighting language** to every interactive tile (action/assert nodes, branch nodes, entity cards, fork/join bars,
the fork block, the Phase-1 toggle pill): a per-tile directional fill/border/gloss shaded by one light, a
cursor-tracked surface **sheen** + far-edge rim **glint** revealed on hover, a **time-of-day sun** that sets the
light angle + a `--sun` intensity multiplier, an eased `requestAnimationFrame` loop, **corner-bracket** keyboard
focus, and a `prefers-reduced-motion` fallback. Metaphor is *light on glass*, not *raised UI* (no drop shadows,
no positional lift).

**The visual language is already locked** — it is not re-litigated here:
- Spec §6–§8 of `docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md` (sun math, easing, inner
  gradient, sheen/glint, focus brackets, coexistence, theme vars).
- Locked reference mock `.git/sdd/mockup-hover-sheen.html` (the visual authority for §7).

**This doc's contribution** is the architecture that maps that single-`<svg>` mock onto the report's **N**
per-scenario SVGs. The five open multi-SVG concerns (from the Phase-1 plan's "Phase 2" section) are resolved in
§4. The C# model + builder are **unchanged**, and the `HtmlReportModelBuilderTests` `Verify(json)` snapshot
**must not change** — the whole feature is template-side.

## 2. Binding constraints (unchanged)

- **VCS = `jj` only** (colocated repo; read-only `git` ok). No `Co-Authored-By` / tooling trailers.
- Single self-contained template `src/Raun.Mtp/HtmlReport/report-template.html` (inline HTML/CSS/JS; model
  injected at the one `<script id="model" .../*__RAUN_REPORT_JSON__*/>` token). **No external
  URL/CDN/web-font/`@import`** — only the SVG-ns literal `http://www.w3.org/2000/svg`. Source Serif 4 stays
  base64-embedded. Time-of-day uses the **client's local clock** (`new Date()`) — no network.
- **C# `HtmlReportModel` + builder UNCHANGED**; `HtmlReportModelBuilderTests` snapshot unchanged; keep
  `HtmlReportSinkTests` green; `dotnet build Raun.slnx -warnaserror` is **0 warnings** (240-test baseline).
- Define **both palettes** (`--ad-*` / `--ph-*` + the new lighting vars) for light + dark.
- **NO decision/merge diamonds** (model is a step-DAG; out of scope).

## 3. Architecture — two decoupled lighting paths

The single-SVG mock conflates two effects the report must separate. The whole design follows from splitting them:

| | **Sun** (slow, global) | **Cursor** (fast, local) |
|---|---|---|
| Drives | every tile's resting fill/border/gloss angle, in **every** SVG | the surface **sheen** + rim **glint**, in **only the hovered** SVG |
| Cadence | at build time + after `renderAll()` + `setInterval` ~60s | `requestAnimationFrame`, only while a pointer is over a diagram |
| Writes | per-instance `userSpaceOnUse` dir-gradients, gathered fresh from live DOM each tick | exactly **2** nodes — the active SVG's `#sheen-<id>` / `#edge-<id>` |
| State | module `sunDir = {dx, dy}` (unit) + `--sun` CSS var on `:root` | eased cursor centre + eased light direction, for the one active SVG |

**Locked decision D1 — scope = global sun + hovered-SVG cursor.** Every tile in every SVG is lit by the
time-of-day sun. The cursor-tracked sheen + rim glint live in only the scenario SVG the pointer is currently
over; all other SVGs rest at sun-only. This is the literal "one sun over the page; a local focus you carry
between cards" reading, and it bounds the fast path so it never rewrites more than 2 gradient nodes per frame
(spec §9's allowed perf decoupling, taken by construction).

**Locked decision D2 — per-tile gradients follow only the sun (decoupled).** The cursor does **not** nudge
per-tile angles. Per-tile `ad-dir` gradients are written at build time and re-walked on the ~60s sun clock; only
the 2 shared sheen/edge gradients track the cursor. This is spec §9's decoupling taken fully: it makes the fast
path tiny and dissolves concern #3 (no persistent per-tile registry to prune). Cost: it drops the mock's
barely-perceptible per-tile "breathing" on hover — an accepted, near-invisible deviation from the single-SVG
authority (the anchor is far, so the mock's nudge is already sub-perceptual).

## 4. The five multi-SVG concerns — resolutions

### 4.1 Concern #1 — per-SVG gradient instances

Each scenario's `<defs>` (built in `buildActivityDiagram`, already carrying the per-scenario-unique
`ad-arrow-<scenarioId>` marker) also receives:

- `#sheen-<scenarioId>` — radial `userSpaceOnUse`, the surface wash for that SVG (mock `#sheen`).
- `#edge-<scenarioId>` — linear `userSpaceOnUse`, the rim glint for that SVG (mock `#edge`).
- The lit-tile dir-gradients, each `class="ad-dir"`. A tile emits **three** (`fill`, `border`, `gloss`) because
  their *stops* differ (fill `base@1→.95`, border `hue@.9→.5`, gloss `white@.12→0`), but all three share
  identical **geometry** — `cx`/`cy`/`L` and endpoints — so the sun pass updates them uniformly. Each `ad-dir`
  element carries `data-cx`, `data-cy`, `data-l` so any pass can recompute its endpoints from `sunDir` with **no
  stored element references**.

The mock's id-keyed stop CSS (`#sheen .ss0 { … }`, `#edge .es2 { … }`) is **re-keyed by class** so one rule
serves all N instances:

```css
.ad-sheen .ss0{ stop-color:var(--sheen); stop-opacity:calc(var(--sheen-peak) * var(--sun)); }
.ad-sheen .ss1{ stop-color:var(--sheen); stop-opacity:calc(var(--sheen-peak) * var(--sun) * 0.3); }
.ad-sheen .ss2{ stop-color:var(--sheen); stop-opacity:0; }
.ad-edge  .es0{ stop-color:var(--edge);  stop-opacity:0; }
.ad-edge  .es2{ stop-color:var(--edge);  stop-opacity:calc(var(--edge-peak)  * var(--sun)); }
```

(`#sheen-<id>` / `#edge-<id>` elements also carry `class="ad-sheen"` / `class="ad-edge"`.) `--sun`,
`--sheen-peak`, `--edge-peak` remain the calc inputs, so retuning per theme stays a variable change.

### 4.2 Concern #2 — cursor → which-SVG mapping

One **delegated `pointermove` listener on the persistent `report` container** (not per-SVG — `report` survives
rerender; per-SVG listeners would need re-attaching on every `replaceChild`):

- `const svg = e.target.closest('svg.actdiag')` → the active SVG (or none if the pointer is between cards).
- On a **switch** of active SVG: capture the new SVG's `#sheen-<id>` / `#edge-<id>` nodes into the fast-path
  state; the previously-active SVG simply stops being written (its sheen/glint fade out via CSS `:hover`).
- Transform the pointer into the **active SVG's own user space** via `svg.getScreenCTM().inverse()` (each SVG
  has its own CTM; this is the multi-SVG generalization of the mock's single `getScreenCTM`).
- Set the cursor target + light-direction target (`normalise(anchor → cursor)`), exactly as the mock. The
  far-edge glint span is computed from the hovered tile's `getBBox()` (`e.target.closest('.ad-lit')`).

Only the hovered SVG is ever cursor-lit; others rest at sun-only (D1).

### 4.3 Concern #3 — rerender pruning

Dissolved by D2. `rerender()` rebuilds one scenario card (`replaceChild`), detaching its old SVG + gradients.
Because **no module state holds references to per-tile gradients** (they are written at build time and re-found
by `querySelectorAll` each sun tick over **live DOM**), a rerender leaves nothing stale to prune on the slow
path. The fast path holds only the *active* SVG's 2 sheen/edge nodes, captured on pointer-enter; a rerender of a
non-hovered card cannot affect them, and the active refs carry an `el.isConnected` guard as cheap safety (skip a
write if the node was detached out from under the loop). A freshly rerendered SVG is **born correctly lit**: its
`buildActivityDiagram` writes every tile's endpoints from the current `sunDir` at build time (no flash, no
post-render fix-up pass needed).

### 4.4 Concern #4 — performance at report scale

- **Fast path** (per frame, while hovering): the guarded rAF writes the active SVG's sheen centre + one tile's
  edge span — **2 gradient nodes**, regardless of report size. The mock's `> 0.0008` direction-change guard is
  kept, so the loop settles and stops doing work when idle.
- **Slow path** (per ~60s tick): `applySun()` walks `querySelectorAll('.ad-dir')` over all live SVGs and
  rewrites endpoints. Cheap and infrequent; the sun barely moves between ticks. Build-time lighting means new
  cards never wait for a tick to look right.

### 4.5 Concern #5 — interaction with Phase-1's pill

**Locked decision D3 — uniform corner-bracket focus everywhere, including the pill.** Every interactive tile —
action/assert nodes, branch nodes, cards, fork/join bars, fork block, **and** the Phase-1 `.ad-fork-seg` pill
segments — gets `tabindex=0 role=button` + an `aria-label` + the corner-bracket `:focus-visible` treatment
(spec §7.5; `L≈5.4`, offset `≈1.7`, stroke `≈0.85`, `var(--ring)`). The Phase-1 outline on `.ad-fork-seg` is
**replaced** by brackets; bracket geometry is tuned tight for the small (~11px) segment. The **active pill
segment picks up a lit `ad-dir` gradient** (spec G5), so it shades under the same sun as the tiles. Bracket
focus composes with the existing click→`focusStep` and the Phase-1 toggle keydown.

## 5. Coexistence + reduced motion

- **Path-emphasis (shipped) is untouched.** Hovering a card/edge still fires `.has-em` on the diagram, dimming
  off-path elements to opacity ≈ 0.24. The hovered tile keeps full opacity + its sheen/glint while off-path
  flows dim (verified in the mock — sheen reads clearly against dimmed neighbours). Default (no hover) stays
  flat/whisper-subtle.
- **`prefers-reduced-motion: reduce`:** no rAF, no cursor tracking, no fades. The sun is fixed at a pleasant
  static angle (noon / upper-left); per-tile `ad-dir` gradients are lit once at that angle (no 60s interval
  needed under reduced motion); the cursor-driven sheen/glint are **suppressed**. Tiles keep their lit
  fill/border/gloss + bracket focus, so the diagram is fully legible and keyboard-operable with zero motion.

## 6. Theme variables

Add to both `:root` and `:root[data-theme=light]` (final mock values, spec §8):

| var | dark | light | role |
|---|---|---|---|
| `--bright` | `1.03` | `1.03` | faint hover surface brightness |
| `--sheen` / `--sheen-peak` | `#ffffff` / `0.06` | `#bcd4ff` / `0.14` | surface wash colour / peak |
| `--edge` / `--edge-peak` | `#ffffff` / `0.5` | `#ffffff` / `0.48` | rim-glint colour / peak |
| `--sun` | `1` (set by JS) | `1` (set by JS) | time-of-day intensity multiplier |
| `--ring` | accent | accent | focus brackets |

Per-instance fills/borders/gloss reuse existing `--ad-*` / `--ph-*` / type-colour tokens; only the geometry +
opacity stops are new. White sheen on white tiles (light theme) is intentionally faint; the tint shifts blue
(`--sheen`) so it stays visible.

## 7. Test & build impact

- **C# unchanged** → `HtmlReportModelBuilderTests` `Verify(json)` snapshot unchanged; `HtmlReportSinkTests`
  green. Whole feature is template-side (HTML/CSS/JS); no JS test runner is added (none exists — same as the
  shipped diagram + Phase 1).
- **0-warning build** (`dotnet build Raun.slnx -warnaserror`); full suite green (`dotnet test Raun.slnx -c
  Debug`, 240 baseline; do **not** pass `--nologo` — Raun is an MTP framework and rejects it).
- **Verify loop:** `dotnet run --project samples/AppointmentTests -c Debug -- --report-html` (rebuilds Raun.Mtp
  → re-embeds the template) → `samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html`.
  Headless: `npx playwright screenshot --browser=chromium --full-page` (chromium installed; the Playwright MCP
  defaults to Chrome which is NOT). **Force theme via `?theme=dark|light`** (the CLI defaults to light). For the
  cursor-tracked sheen/glint + which-SVG mapping + time-of-day, drive with a Node Playwright script
  (`getScreenCTM` mouse moves across two scenario cards; set/stub the local clock). Verify the
  `prefers-reduced-motion` path renders static. The fork case = the **"customer books with parallel arrange"**
  scenario; verify a **second** scenario card lights independently (the multi-SVG proof).
- **Both palettes** headless-verified; reduced-motion verified static.

## 8. Anticipated task shape (for the plan)

Finalized in `writing-plans` → `docs/superpowers/plans/2026-06-21-report-sun-lighting.md`:

1. Theme vars (both palettes) + reduced-motion CSS + the class-keyed `.ad-sheen`/`.ad-edge` stop CSS.
2. Per-scenario `#sheen-<id>`/`#edge-<id>` defs + the `ad-dir` gradient helper (build-time sun-lit, carries
   `data-cx/cy/l`) + lit fill/border/gloss on **nodes + branch nodes**.
3. Same lit treatment on **cards + fork/join bars + the fork block**.
4. The sun slow pass: `sunDir` module state + `applySun()` (time-of-day → anchor + `--sun`, real local clock) +
   the ~60s interval, plus build-time lighting in `buildActivityDiagram`.
5. The cursor fast path: delegated `pointermove` on `report` + active-SVG switching + per-SVG `getScreenCTM` +
   the eased guarded rAF + `isConnected` guard + reduced-motion skip.
6. Hover **sheen** + rim **glint** overlays per tile (the `.ad-lit` hover rects referencing
   `#sheen-<id>`/`#edge-<id>`), coexisting with the shipped `.has-em` path-emphasis.
7. Uniform corner-bracket `:focus-visible` + `tabindex/role/aria` on **all** tiles incl. the pill segments
   (replacing the Phase-1 outline) + lit active segment (G5) + keydown parity.

Optional fold-in (review-sanctioned Phase-1 deferrals, non-blocking): extract `applyNodeStatusStyle()` /
`segmentBar()`; rename `AD_GRAPH_JOIN_H`.

## 9. Out of scope / deferred

- Per-step duration chips; decision/merge diamonds (no model branch data); persisting light state across reloads
  (session-only / clock-driven by design); the mock's `▶ play day` scrubber (dev-only, not shipped).
- The mock's per-tile cursor nudge (dropped by D2 — sun-only per-tile).

## 10. Reference

- Visual authority: `.git/sdd/mockup-hover-sheen.html` (locked); sun math/easing/gradient/focus detail in
  `docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md` §6–§8 + §10.
- Template integration points (anchor on function names; line numbers drift): `buildActivityDiagram` (per-SVG
  `<defs>`), `actionNode` / `buildForkGraph` / `buildForkCell` (tiles), `cardEl` family (cards), `renderScenario`
  / `rerender` (the `replaceChild` swap), `renderAll` + `document.fonts.ready` (full re-render — gradients must
  survive it), `svgEl` (createElementNS helper), `buildScenarioCard` (delegated listeners + Phase-1 pill focus).
</content>
</invoke>
