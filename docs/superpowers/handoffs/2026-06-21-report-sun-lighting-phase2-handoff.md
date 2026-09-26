# Handoff — Phase 2: sun-driven lighting for the Raun HTML report

> Paste the KICKOFF block below into a fresh context window to continue. **Phase 1 (fork graph view +
> per-fork toggle) is DONE and landed on `main`** (`e9f30a4d`). Phase 2 = the report-wide "sun-driven
> lighting" language — **spec'd but deferred**, with 5 open multi-SVG concerns to resolve BEFORE it can be
> planned concretely. No Phase 2 code written yet.

## ▶ PASTE-READY KICKOFF (copy this into the new chat)

> Continue the **Raun HTML-report** work: implement **Phase 2 — "sun-driven lighting"** (a report-wide
> single-light shading/sheen/glint language). Phase 1 (fork graph view + per-fork toggle) already shipped to
> `main`. **Read first, in order:** (1) this handoff
> `docs/superpowers/handoffs/2026-06-21-report-sun-lighting-phase2-handoff.md` in full; (2) the spec §6–§8
> `docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md`; (3) the **"Phase 2" section** of the
> plan `docs/superpowers/plans/2026-06-21-fork-graph-view-toggle.md` (it lists the 5 open concerns + the
> anticipated task shape); (4) the **locked mock** `.git/sdd/mockup-hover-sheen.html` — open it in a browser
> (`Start-Process`) and headless-render it; it is the visual authority; (5) memory
> `raun-report-activity-diagram`; (6) the progress ledger `.git/sdd/fgv-progress.md`. Then: **the first job
> is to RESOLVE the 5 open multi-SVG concerns** (see this handoff) — they're genuine design decisions the
> single-SVG mock doesn't cover, so use `superpowers:brainstorming` with me to lock the approach (especially
> per-SVG gradient instances vs. light-only-the-hovered-SVG). Then `superpowers:writing-plans` →
> `docs/superpowers/plans/2026-06-21-report-sun-lighting.md` → `superpowers:subagent-driven-development`.
> Constraints: jj-only (never git mutations), no trailers, single self-contained template (no
> CDN/web-font/@import), C# model/snapshot unchanged, both light+dark palettes, 0-warning build.

## What Phase 2 is (the feature)

One coherent model: **a single light over the whole diagram**, positioned where the **sun** would be for the
current time of day. Every interactive tile (action/assert nodes, branch nodes, entity cards, fork/join bars,
the fork block) is shaded by that one light; hovering a tile catches a soft specular **sheen** + a sharp rim
**glint**; keyboard focus draws **corner brackets**. Metaphor is *light on glass*, not *raised UI* (no drop
shadows / no positional "lift"). It **absorbs and replaces** the earlier flat "gradient flair" idea — the
gradients *are* the lighting now. Full detail is in **spec §6–§8**; the **locked reference is
`.git/sdd/mockup-hover-sheen.html`** (live: sun by time-of-day, eased single-light sheen + far-edge rim glint,
whisper-subtle per-instance fixed-length `userSpaceOnUse` gradients, corner-bracket focus, both themes; its
`▶ play day` slider is a dev/demo control only, NOT shipped).

Key spec mechanics (from §7, all in the mock):
- **Sun:** `β = (hours−12)·π/12` (full 24h circle, continuous at midnight); anchor far off in the sun's
  direction; intensity `--sun = 0.65 + (elev≥0 ? 0.5 : 0.12)·elev`, `elev = cos β` (noon ≈1.15, dusk ≈0.65,
  midnight ≈0.53). Uses the **client's local clock** (`new Date()`) — self-contained; recompute on load + a
  slow interval (~60s).
- **Per-tile inner gradient:** `userSpaceOnUse` (NOT `objectBoundingBox` — that distorts by aspect ratio),
  **fixed length `L = max(w,h)`**, centred, endpoints `center ± dir·L/2`, rotated to the light. Fill is a
  ~5% same-hue fade (nearly flat); border carries the visible directional read (`hue@0.9 → hue@0.5`); a faint
  white gloss (`@0.12 → 0`).
- **Hover sheen** (broad radial `#sheen`, r≈165, peak 0.06 dark / 0.14 light) + **rim glint** (linear `#edge`,
  sharp cutoff, lands on the **far** edge away from the sun, peak 0.5 / 0.48), both scaled by `--sun`, revealed
  on hover with a gentle fade (in 0.22s / out 0.5s).
- **Easing:** a single guarded `requestAnimationFrame` loop eases surface centre toward the cursor (0.12/frame)
  and light direction toward target (0.085/frame); only rewrites gradients when the eased dir changed `>0.0008`
  (settles when idle). Under `prefers-reduced-motion: reduce`: skip the loop, static light at a fixed pleasant
  angle, no cursor tracking, no fades.
- **Focus = corner brackets** (`var(--ring)` reticle, L≈5.4, offset≈1.7, sw≈0.85), on `:focus-visible`,
  distinct from hover. Every interactive tile becomes `tabindex=0 role=button` with `aria-label`.

## ⚠ THE CRUX — 5 open concerns to resolve BEFORE planning (the mock is ONE svg; the report has N)

The locked mock is a single `<svg>`. The report renders **one `<svg class="actdiag">` per scenario**, each with
its own `viewBox`/coordinate space. The cursor-tracked `userSpaceOnUse` gradients cannot be one global gradient.
Resolve these with the user (brainstorm) first — they change the task breakdown:

1. **Per-SVG gradient instances.** `#sheen`/`#edge` and every per-tile gradient are `userSpaceOnUse` (tied to a
   coordinate space). A single global gradient can't serve N SVGs. Likely resolution: per-scenario instances
   (id suffixed by `scenarioId`, appended to each SVG's existing `<defs>`), plus a registry that knows which
   SVG each belongs to.
2. **Cursor → which-SVG mapping.** The mock uses one `getScreenCTM`. The report must detect which scenario SVG
   the pointer is over and transform into *that* SVG's user space — or light only the hovered SVG (others rest
   at sun-only). Pick one.
3. **Rerender pruning.** `rerender()` rebuilds ONE scenario card (`replaceChild`), detaching its gradient nodes.
   The global rAF registry must drop detached entries (`el.isConnected` filter) so it doesn't leak or write to
   dead nodes. (Same "stale-entry per re-render" class noted as a deferred minor in the shipped diagram; the
   Phase-1 toggle now also triggers rerenders, so this matters more.)
4. **Performance at report scale.** Keep the rAF guard. With many scenarios/tiles, consider decoupling the slow
   sun clock (per-tile fills/borders) from the fast cursor track (only the 2 shared sheen/edge gradients). Spec
   §9 explicitly allows this optimization.
5. **Interaction with Phase 1's pill.** The active toggle-pill segment should pick up the lighting (spec G5),
   and the pill segments take the bracket focus (they currently use a `:focus-visible` outline). Small
   integration once both exist. Phase 1 already added `tabindex/role/aria` + `:focus-visible` to the pill
   segments (`.ad-fork-seg`) — generalize the bracket-focus to ALL tiles.

## Anticipated Phase 2 task shape (from the plan; finalize after the 5 are resolved)

(a) theme vars + reduced-motion CSS + per-scenario shared `#sheen`/`#edge` defs + the per-instance gradient
registry; (b) lit fill/border/gloss on nodes + branch nodes; (c) same on cards + fork/join bars + the fork
block; (d) the sun (time-of-day → anchor + `--sun`, real local clock, slow interval); (e) the eased rAF loop +
cursor tracking + `isConnected` pruning + reduced-motion skip; (f) hover sheen + rim glint overlays (coexisting
with the shipped `.has-em` path-emphasis); (g) corner-bracket `:focus-visible` + `tabindex/role/aria` on all
tiles + keydown parity.

## Template integration points (anchor on function NAMES — line numbers have drifted post-Phase-1)

All in `src/Raun.Mtp/HtmlReport/report-template.html` (grep for these):
- **Tiles to light:** `actionNode` (spine nodes); `buildForkGraph` (the Phase-1 branch nodes + fork/join bars);
  `buildForkCell` (timeline fork/join bars + lane bars); `cardEl` / `stackCardEl` / `greyCardEl` (entity cards).
- **Per-scenario SVG + defs:** `buildActivityDiagram` builds each `<svg class="actdiag">` and its `<defs>`
  (where the arrowhead marker lives) — per-scenario gradient instances go here.
- **Rerender + module state:** `renderScenario`/`rerender` (the `replaceChild` swap); follow the existing
  module-level state pattern (`adExpansion`/`expansionFor`, `adForkView`/`forkViewFor`).
- **Coexistence:** the shipped object-path emphasis is CSS `.ad-flow.has-em [data-obj]:not(.em){opacity:.22}`
  / `.em{opacity:1}` (delegated mouseover/mouseout on the `.ad-flow` group). The new per-tile sheen must NOT
  fight it: the hovered tile keeps full opacity + its sheen while off-path flows dim. Verify in the mock — it
  reads clearly against dimmed neighbours.
- **Phase-1 focus precedent:** `.ad-fork-seg:focus-visible` + `tabindex/role/aria` on pill segments (CSS near
  the `.ad-fork*` rules). Generalize bracket focus to every tile.
- **`svgEl(tag, attrs)`** is the createElementNS helper; **`document.fonts.ready.then(renderAll)`** re-renders
  after font load (gradients must survive a full re-render). **`measureText`** exists for sizing.

## Binding constraints (unchanged from Phase 1)

- **VCS = `jj` only**, never `git` mutations (colocated repo; read-only `git/jj` status/log/diff ok). No
  `Co-Authored-By`/tooling trailers. Don't move the `main` bookmark without user consent.
- **Single self-contained template** `src/Raun.Mtp/HtmlReport/report-template.html` (inline HTML/CSS/JS; model
  injected at the one `<script id="model" .../*__RAUN_REPORT_JSON__*/>` token). **No external
  URL/CDN/web-font/`@import`** — only the SVG-ns literal `http://www.w3.org/2000/svg`. Source Serif 4 stays
  base64-embedded. Time-of-day uses `new Date()` (local clock) — no network.
- **C# model + builder UNCHANGED**; `HtmlReportModelBuilderTests` `Verify(json)` snapshot must NOT change; keep
  `HtmlReportSinkTests` green; **0-warning build** (`dotnet build Raun.slnx -warnaserror`).
- **Both palettes** (`--ad-*` / `--ph-*` + the new lighting vars `--bright`, `--sheen`/`--sheen-peak`,
  `--edge`/`--edge-peak`, `--sun`, `--ring`) defined for light + dark. White sheen reads stronger on light —
  tune per theme (mock has the values).

## Verify loop (impl phase)

`dotnet run --project samples/AppointmentTests -c Debug -- --report-html` (rebuilds Raun.Mtp → re-embeds the
template) → `samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html`. Headless: `npx
playwright screenshot --browser=chromium --full-page` (chromium IS installed; the Playwright MCP defaults to
Chrome which is NOT — use the CLI). **Force theme via `?theme=dark|light`** (the CLI defaults to light — a
no-param "dark" capture silently renders light). For the cursor-tracked sheen / glint side / time-of-day, drive
with a Node Playwright script (hover + `getScreenCTM` mouse moves; set/stub the local clock). Verify the
`prefers-reduced-motion` path renders static. Full C# suite: `dotnet test Raun.slnx -c Debug` (240 baseline;
do NOT pass `--nologo` — Raun is a Microsoft.Testing.Platform framework and rejects it). Root `*.png`/`*.cjs`
scratch is gitignored.

## Process

1. Read the refs (KICKOFF list). Open `mockup-hover-sheen.html` live + headless.
2. `superpowers:brainstorming` with the user → **lock the 5 open concerns** (esp. #1/#2). Optionally one mock
   round adapting the single-SVG mock to a 2-scenario report case.
3. `superpowers:writing-plans` → `docs/superpowers/plans/2026-06-21-report-sun-lighting.md`. Self-review,
   user-review gate.
4. `superpowers:subagent-driven-development` → implement task-by-task (fresh implementer + task review each,
   broad final review). Keep a ledger (e.g. `.git/sdd/sun-progress.md`). Land via
   `superpowers:finishing-a-development-branch` (jj — advance `main` only with consent; local-only, no remote).

## State at handoff

- `main` = `e9f30a4d` (Phase 1 landed: T1 graph view, T2 toggle, fix). 240/240 green, 0 warnings, both themes.
- Phase 1 ledger + all briefs/reports/diffs under `.git/sdd/fgv-*`. Locked Phase-2 mock:
  `.git/sdd/mockup-hover-sheen.html`. Deferred Phase-1 cleanups (non-blocking, could fold into Phase 2):
  extract `applyNodeStatusStyle()` / `segmentBar()` (plan-mandated dups), rename `AD_GRAPH_JOIN_H`.
