# Design — Fork "graph view" + per-fork view-toggle + sun-driven lighting (Raun HTML report)

> Status: **design, approved to spec** (2026-06-21). Builds on the shipped activity diagram
> (`src/Raun.Mtp/HtmlReport/report-template.html`, main `4c9df0d8`). No code written yet.
> Terminal step of this doc: user review → `writing-plans` → `subagent-driven-development`.

## 1. Summary

The Raun HTML report renders a per-scenario **SVG activity diagram** (GWT swimlanes; initial → action/assert
nodes → final ring; objects as entity cards crossing fork walls). Today a **fork** renders only as the inline
"timeline cell" (`buildForkCell`, a cropped Gantt). This feature adds:

1. A second fork rendering — a **standard UML graph view** (fork bar → parallel branch nodes → join bar) — and
   makes it the **default**; the timeline becomes opt-in.
2. A **per-fork, hover-revealed segmented pill** to toggle a fork between *graph* and *timeline* in place.
3. A **single-light "sun" lighting language** applied uniformly to every interactive tile (nodes, cards, branch
   nodes, fork/join bars, fork block): a soft directional sheen + rim glint that tracks one light source whose
   angle and brightness follow **time of day**, plus a keyboard focus treatment. This **absorbs and replaces the
   earlier flat "gradient flair" idea** — the gradients *are* the lighting now.

All of this lives in the single embedded template; the **C# model + builder are unchanged**, and the
`HtmlReportModelBuilderTests` `Verify(json)` snapshot **must not change**.

## 2. Binding constraints (unchanged)

- **VCS = `jj` only** (colocated repo; read-only `git` ok). No `Co-Authored-By` / tooling trailers.
- Single self-contained template `src/Raun.Mtp/HtmlReport/report-template.html` (inline HTML/CSS/JS; model
  injected at the one `<script id="model" .../*__RAUN_REPORT_JSON__*/>` token). **No external URL/CDN/web-font/
  `@import`** — only the SVG-ns literal `http://www.w3.org/2000/svg` is allowed. Source Serif 4 stays base64-embedded.
  Time-of-day uses the **client's local clock** (`new Date()`) — no network, fully self-contained.
- **C# `HtmlReportModel` + builder UNCHANGED**; `HtmlReportModelBuilderTests` snapshot unchanged; keep
  `HtmlReportSinkTests` green; `dotnet build Raun.slnx -warnaserror` is **0 warnings**.
- Define **both palettes** (`--ad-*` / `--ph-*` and the lighting vars below) for light + dark.
- **NO decision/merge diamonds** (model is a step-DAG; out of scope).

## 3. Locked decisions (do not re-litigate)

These were locked before/within the brainstorm and are inputs to the plan:

- **G1. Graph view = pure UML structure, no timing.** Spine → solid **fork bar** (UML sync bar) → parallel
  **branch nodes** (each a content-sized, phase-tinted action node for that lane's step) → solid **join bar** →
  back to spine. Object entity cards still hang off each branch (same producer-disc → card → consumer grammar).
- **G2. Graph view is the DEFAULT** fork rendering; the timeline cell becomes opt-in via the toggle.
- **G3. Toggle is per-fork, session-only** (a module-scope map like the existing `adExpansion`; not persisted,
  resets on reload).
- **G4. Toggle affordance = hover-revealed segmented pill, NOT a persistent icon.** Hovering a fork block reveals
  a small near-square pill (rx ≈ 5, thin border) reading **`show as [ graph | timeline ]`**; clicking the other
  segment swaps that fork in place; everything vanishes on hover-off (zero chrome at rest).
- **G5. Pill grouping = the all-text variant** (`mockup-flair.html` §2-i): a muted `show as` label, then the two
  options `graph` / `timeline` enclosed in their own bordered **track** (reads as one toggle). No icon/eye, no
  parentheses. Active segment carries the lighting (see §7).
- **G6. Join-bar = option A**: a slate join sync-bar with **small gaps** where object lines pass straight through
  (mirrors the timeline cell's join-wall gaps).
- **G7. Per-step duration chips = DEFERRED** (out of scope; possible future add).
- **G8. Hover/focus + gradient = the sun-driven lighting language in §6–§7** (this brainstorm's main output).

## 4. Graph-view geometry & layout

The fork renderer gains a second mode. Both modes are produced by the **fork renderer** and emit the **same port
contract** (§5) so the object-flow pass is view-agnostic.

**Graph view** (default), top to bottom inside the Given band:

1. Spine enters → **fork bar**: a solid slate horizontal sync-bar spanning the branch span (reuse the timeline
   cell's heavy-slate bar treatment; lit per §7).
2. **Branch nodes**: one phase-tinted action node per lane, laid out left→right with even gutters, each
   content-sized to its step label (same node grammar as the spine's action boxes).
3. **Object entity cards** hang off each branch node: producer **disc port** sits at the **bottom edge of the
   branch node** (this is the relocation vs the timeline cell — see §5); card below it; consumer edges leave per
   the existing `buildObjectFlow` grammar.
4. **Join bar**: option A slate sync-bar with gaps where object lines pass through → spine resumes.

**Timeline view** (opt-in): unchanged `buildForkCell` (cropped inline Gantt), now reached via the toggle.

**Band sizing.** Graph view changes the fork region's height/shape vs the cell, so `layoutScenario` must size the
Given band from **whichever view is active** for each fork. The collapse-tier work already reflows the band via
rerender, so this pattern exists — extend it to read the active fork view.

## 5. Architectural crux — the shared `it.ports` contract

In the timeline cell, `buildForkCell` exposes `it.ports` = each lane's **disc port** (production end), and
`buildObjectFlow` attaches producer → card → consumer edges to those ports. **The graph view relocates the
producer ports** to the bottom of each branch node.

**Design rule:** the fork renderer emits the **same `it.ports` contract** (same shape/fields: per-lane producer
port coordinate + orientation) from *whichever* view is active. `buildObjectFlow` consumes `it.ports` and stays
**view-agnostic** — it does not know or care which view produced the ports. A fork's view choice is therefore a
layout **variant**, not a second drawing pass bolted onto object-flow.

Concretely:
- Factor port emission so both `buildForkGraph` (new) and `buildForkCell` (existing) return `{ ports, bbox,
  svg }` with identical `ports` semantics.
- `buildObjectFlow` reads `ports` and draws producer→card→consumer exactly as today.
- Timeline ports sit on the cell's production wall; graph ports sit at branch-node bottom-centre (clamped/stacked
  per the existing `clampStack` inset rules so multiple objects off one branch don't collide).

## 6. Toggle interaction & rerender wiring

- **Hover a fork block** → the block gets its lighting hover state (§7) **and** the segmented pill appears
  (`show as [ graph | timeline ]`, active segment highlighted). Hover-off → pill and hover state fade out.
- **Click the inactive segment** → set this fork's view in the session map (G3), then call the **existing
  per-scenario `rerender`** (the same machinery the collapse-tier click uses) with the fork's view flipped.
  Rerender re-runs `layoutScenario` (which sizes the band from the active view, §4) and re-emits ports →
  `buildObjectFlow` reattaches edges to the new port positions. No bespoke animation; it's a re-render.
- **State map** `adForkView` (module scope, mirrors `adExpansion`): `forkId → 'graph' | 'timeline'`, default
  `'graph'` when absent, survives rerender, resets on reload.
- Keyboard: the pill segments are focusable buttons (role=button, tabindex), so the toggle is operable without a
  mouse; they take the §7 focus treatment.

## 7. The lighting language (sheen, glint, gradient, focus) — locked reference `.git/sdd/mockup-hover-sheen.html`

One coherent model: **a single light over the whole diagram**, positioned where the **sun** would be for the
current time of day. Every tile is shaded by that one light; hovering a tile catches a soft specular sheen + a
sharp rim glint; keyboard focus draws corner brackets. Nothing implies a pushable button (no drop shadows / no
positional "lift"); the metaphor is *light on glass*, not *raised UI*.

### 7.1 The sun (time-of-day light source)

- A virtual light anchor sits **far off** in the sun's direction. Sun angle over a **full 24h circle** (so
  midnight is continuous, not a clamp):
  - `β = (hours − 12) · π / 12` → `0` at noon (overhead), `±π` at midnight (nadir), `−π/2` at 06:00 (east),
    `+π/2` at 18:00 (west). `hours` = local `getHours() + getMinutes()/60`.
  - `anchor = sceneCenter + (sin β, −cos β) · R`, `R` far beyond the viewport (≈ 1500 user units in the mock; in
    the report use a value comfortably larger than the diagram so the angle is near-constant across tiles).
- **Intensity** (a `--sun` multiplier on the sheen + glint, sinusoidal, continuous at the horizon):
  - `elev = cos β` (1 at noon, 0 at the horizon, −1 at midnight).
  - `--sun = 0.65 + (elev ≥ 0 ? 0.5 : 0.12) · elev` → **noon ≈ 1.15, dawn/dusk ≈ 0.65, midnight ≈ 0.53**.
- **Direction** the tiles are lit from = `normalise(anchor → cursor)`, **eased** (see 7.2). Because the anchor is
  far, the angle is *mostly the same* across the scene (one consistent sun); the cursor only **nudges** it.
- The report uses the **real local clock**; recompute on load + on a slow interval (e.g. every 60s — the sun
  barely moves). The mock's **`▶ play day` slider/button is a dev/demo control only** and is NOT shipped.

### 7.2 Easing / inertia

A single `requestAnimationFrame` loop eases toward targets so nothing snaps:
- surface-wash centre toward the cursor: `cur += (target − cur) · 0.12` per frame;
- light direction toward its target: `dir += (target − dir) · 0.085` per frame (lower = heavier lag).
The loop is **guarded**: gradients only rewrite when the eased direction changed by `> 0.0008`, so it settles and
stops doing work when idle. Under `prefers-reduced-motion: reduce`, skip the loop entirely: static light at a
fixed pleasant angle (upper-left / noon), no cursor tracking, no fades.

### 7.3 Inner gradient (the standard tile background / border) — **whisper subtle**

Per-element **`userSpaceOnUse`** gradients (NOT `objectBoundingBox` — that distorts the gradient length by aspect
ratio and was the root cause of the "harsh on wide tiles" problem). For each tile:
- **Fixed length `L = max(width, height)`**, **centred** on the tile, endpoints `center ± dir · L/2`, rotated to
  the light. Identical per-pixel steepness at every angle; a wide thin tile naturally shows more gradient when lit
  along its long axis and less across its short axis (correct).
- **Fill**: `stop0 = base @1.0`, `stop1 = base @0.95` — a **5% fade**, essentially flat with only a trace of the
  light direction. (This was tuned down hard; the fill must read as nearly uniform.)
- **Border**: `stop0 = hue @0.9`, `stop1 = hue @0.5` — borders carry the visible directional read (brighter on
  the light-facing side). Node border stroke `0.9`, card border `1.1` (thin/muted; nodes sit quieter than cards).
- **Gloss** (node surface highlight): white `@0.12 → 0` over offsets `0 → 0.6`, same geometry — a faint
  light-side sheen.
- All three update together in the guarded rAF step. Fork bars and the fork-block border get the same per-instance
  treatment so the whole scene lines up under one light.

### 7.4 Hover sheen (surface) + rim glint (edge)

Two shared `userSpaceOnUse` gradients, both scaled by `--sun`, revealed only on hover with a **gentle fade
(in `0.22s`, out `0.5s`)** — no harsh on/off:
- **Surface sheen** `#sheen`: a broad radial wash (r ≈ 165), peak `--sheen-peak` `0.06` dark / `0.14` light,
  centred at the eased cursor. "Sun, not spotlight" — broad and faint; it's the diffuse pool the cursor carries.
- **Rim glint** `#edge`: a **linear** gradient painted on a perimeter stroke matched to the tile's own border
  width (so it brightens the edge without thickening it). **Sharp cutoff** (stops `0`, `0.5` transparent → `0.66`,
  `1` bright) = a polished specular line, peak `--edge-peak` `0.5` dark / `0.48` light. It lands on the **far
  edge — the one away from the sun** (the raised edge "catching" the raking light), and its width spans the whole
  far edge ("sun" light, so the whole opposite rim is lit). Direction follows the same eased light axis.

### 7.5 Focus (keyboard) = corner brackets

Focus draws four thin **accent corner ticks** (a reticle) hugging the tile — `L ≈ 5.4`, offset `≈ 1.7`, stroke
`≈ 0.85`, butt caps, accent (`--ring`) colour. Distinct from hover by being a discrete marker (not the sheen), so
mouse-hover and keyboard-focus never read the same. Applies uniformly to nodes, cards, and the fork block. Every
interactive tile is `tabindex=0 role=button` with an `aria-label`; this is the diagram's first proper keyboard
focus affordance (it composes with the existing click→`focusStep`).

### 7.6 Coexistence with the shipped path-emphasis

Hovering an object card/edge still fires the shipped path-emphasis (`.has-em` on the diagram, `.em` dimming
off-path elements to **opacity ≈ 0.24**). The per-tile hover sheen/glint must coexist: the hovered tile keeps full
opacity + its sheen/glint while everything off its object path dims. Verified in the mock — the sheen reads
clearly against dimmed neighbours. Default (no hover) stays flat.

## 8. Theme variables

Add lighting vars to both `:root` and `:root[data-theme=light]` (final mock values):

| var | dark | light | role |
|---|---|---|---|
| `--bright` | `1.03` | `1.03` | faint hover surface brightness |
| `--sheen` / `--sheen-peak` | `#ffffff` / `0.06` | `#bcd4ff` / `0.14` | surface wash colour / peak |
| `--edge` / `--edge-peak` | `#ffffff` / `0.5` | `#ffffff` / `0.48` | rim-glint colour / peak |
| `--sun` | `1` (set by JS) | `1` (set by JS) | time-of-day intensity multiplier |
| `--ring` | accent | accent | focus brackets |

Per-instance fills/borders/gloss reuse the existing `--ad-*` / `--ph-*` / type-colour vars; only the geometry +
opacity stops are new. Light theme: white sheen on white tiles is intentionally faint; the sheen tint shifts blue
(`--sheen`) so it stays visible.

## 9. Test & build impact

- **C# unchanged** → `HtmlReportModelBuilderTests` `Verify(json)` snapshot unchanged; `HtmlReportSinkTests`
  green. The whole feature is template-side (HTML/CSS/JS).
- **0-warning build** (`dotnet build Raun.slnx -warnaserror`).
- **Verify loop** (impl phase): `dotnet run --project samples/AppointmentTests -c Debug -- --report-html`
  (rebuilds Raun.Mtp → re-embeds template) → `samples/AppointmentTests/bin/Debug/net10.0/TestResults/
  raun-report.html`. Fork test case = the **"customer books with parallel arrange"** scenario. Force theme via
  `?theme=dark|light` (Playwright CLI defaults to light). Headless: `npx playwright screenshot
  --browser=chromium` (chromium installed; Playwright MCP defaults to Chrome which is NOT). To verify the
  cursor-tracked lighting / glint side / time-of-day, drive with a Playwright script (hover + `getScreenCTM`
  mouse moves + set the local clock or a debug time hook), as done for the mockups.
- **Both palettes** headless-verified; **`prefers-reduced-motion`** path verified static.
- **Performance:** the guarded rAF (7.2) means no work when idle; per-instance gradient rewrites happen only while
  the light direction changes. For large diagrams, the inner fills/borders may be decoupled to follow only the
  (slow) sun clock while the cursor tracks just the 2 shared sheen/edge gradients — note this as an allowed impl
  optimisation if profiling shows the per-tile rewrites are hot.

## 10. Reference mockups (`.git/sdd/`, untracked scratch)

- **`mockup-hover-sheen.html`** — the **locked lighting language**, live: sun by time-of-day, eased single-light
  sheen + far-edge rim glint, whisper-subtle per-instance fixed-length gradients, corner-bracket focus, both
  themes, `▶ play day` (dev-only). This is the authority for §7.
- `mockup-toggle.html` — locked toggle interaction (hover → block highlight + segmented pill; click → swap).
- `mockup-flair.html` §2-i — locked pill grouping (G5).
- `mockup-fork-graph.html` / `mockup-options.html` — graph-view geometry + join-bar A reference (G1, G6).

## 11. Out of scope / deferred

- Per-step duration chips (G7).
- Decision/merge diamonds (model has no branch data).
- Persisting toggle/light state across reloads (session-only by design).
- The `▶ play day` time scrubber is a mock/dev affordance, not a report feature.
