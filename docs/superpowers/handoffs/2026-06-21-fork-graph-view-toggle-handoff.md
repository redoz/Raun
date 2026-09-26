# Handoff — Fork "graph view" + per-fork view-toggle (Raun HTML report)

> Paste this whole file into a fresh context window to continue. We are **mid-brainstorm** on a new
> feature; no code written yet. The terminal step of brainstorming is: lock the remaining visuals →
> write the spec → `writing-plans` → implement via `subagent-driven-development`.

## ▶ PASTE-READY KICKOFF (copy this into the new chat)

> Continue a **mid-brainstorm** Raun task: adding a UML **graph view** + per-fork **view-toggle** to the
> HTML-report activity diagram. Use `superpowers:brainstorming` (we're at the "iterate on mockups" step).
> First read: (1) this handoff `docs/superpowers/handoffs/2026-06-21-fork-graph-view-toggle-handoff.md`
> in full, (2) memory `raun-report-activity-diagram` (the shipped diagram this builds on), (3) open the
> mockups under `.git/sdd/` in a browser (`Start-Process`) — especially `mockup-toggle.html` (locked
> interaction) and `mockup-hoverfocus.html` (the live OPEN item). Then resume at **OPEN #2 (hover/focus
> states)** — iterate a few more options with me, lock it, then write the spec
> `docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md`, user-review, `writing-plans`,
> implement. jj-only, no trailers; self-contained template; both themes; C# model/snapshot unchanged.

## What this feature is

The Raun HTML report renders a per-scenario **SVG activity diagram** (already shipped — see memory
`raun-report-activity-diagram`, all in `src/Raun.Mtp/HtmlReport/report-template.html`). Today a
**fork** renders ONLY as the inline-Gantt "timeline cell" (`buildForkCell`). This feature adds a second
rendering — a **standard UML graph view** (fork bar → parallel branch nodes → join bar) — and a
**per-fork toggle** to switch a fork between the two views.

## Project rules (unchanged, binding)

- **VCS = `jj` only**, never `git` mutations (colocated repo; read-only `git log/diff/status` ok). No
  `Co-Authored-By`/tooling trailers in commit messages.
- Single embedded template `src/Raun.Mtp/HtmlReport/report-template.html` (inline HTML/CSS/JS; model
  injected at the one `<script id="model" .../*__RAUN_REPORT_JSON__*/>` token). **Self-contained** — no
  external URL/CDN/web-font/`@import` (only the SVG-ns `http://www.w3.org/2000/svg` literal allowed).
  Source Serif 4 is base64-embedded.
- **C# model + builder UNCHANGED**; `HtmlReportModelBuilderTests` `Verify(json)` snapshot must NOT
  change; keep `HtmlReportSinkTests` green; 0-warning build (`dotnet build Raun.slnx -warnaserror`).
  Both light + dark palettes (`--ad-*` / `--ph-*` vars).
- **NO decision/merge diamonds** (model is a step-DAG, no branch data — out of scope, unchanged).
- main is at the shipped activity diagram (commit `4c9df0d8`); this feature builds on top.

## LOCKED decisions (do not re-litigate)

1. **Graph view = pure UML structure, no timing.** Spine → solid **fork bar** (UML sync bar) → parallel
   **branch nodes** (each = a content-sized, phase-tinted action node for that lane's step) → solid
   **join bar** → back to spine. Object entity cards still hang off each branch (same
   producer-disc → card → consumer grammar as the rest of the diagram).
2. **Graph view is the DEFAULT** fork rendering. The timeline cell becomes opt-in via the toggle.
3. **Toggle is per-fork, session-only** (like the collapse-tier `adExpansion` map — not persisted,
   resets on reload).
4. **Toggle affordance = a hover-revealed segmented pill, NOT a persistent icon.** On hover over a fork
   block: the block gets a highlight (see OPEN #2) and a small pill appears reading **`show as
   [ graph | timeline ]`** with the current view highlighted; clicking the other segment swaps that fork
   in place. Everything vanishes on hover-off (zero chrome at rest). Pill is **near-square** (rx ~5, thin
   border) to match the diagram's design language — NOT a round lozenge.
7. **Pill grouping LOCKED to the all-text version (variant i).** A plain muted `show as` label, then the
   two options `graph`/`timeline` enclosed in their OWN bordered **track** (so they read as one toggle,
   not three equal chips). No icon-only/eye variant. No literal parentheses. The active segment carries a
   subtle gradient fill (see gradient note). Ref: `mockup-flair.html` §2-i.
5. **Join-bar treatment = option A**: a slate **join sync-bar with small gaps** where object lines pass
   straight through (mirrors the timeline cell's join-wall gaps).
6. **Per-step duration chips = DEFERRED.** The earlier idea of a `120ms`-style chip on every node is
   **out of scope for now** ("skip timing for now"). Do not build it; note it as a possible future add.

## OPEN decisions (need a pick — mockups are on disk, render/​open them)

1. **Gradient flair (NEW note — mostly settled, confirm intensity).** Add *subtle* gradients so it looks
   less flat — nothing wild, keep the current colors. Refined direction (per user): **mostly left→right
   with a slight downward tilt** (≈ `x2=1, y2=0.3`, NOT vertical, NOT fully horizontal), **subtle** (low
   stop contrast), **same-hue light→base** — cards keep their type colour (blue/green/orange…), just a
   little pizazz. Applies to: card-header gradient, fork/join **bar** sheen, a faint **node gloss**
   overlay, and the active pill segment. Tune per theme (white-sheen subtler on light). Ref:
   `mockup-hoverfocus.html` `<defs>` (`givenG` / `patG` / `gloss`). → Confirm final intensity.
2. **Hover + focus states for EVERY element (NEW note — the big open one).** Both earlier ideas are
   **rejected**: blur-**glow reads tacky**; the flat full **outline reads bland/overdone**. New
   direction: *every* interactive element — the fork **block**, action/assert **nodes**, object
   **cards** — should get a cohesive, **modern-minimal HOVER and FOCUS state**. Three starter treatments
   are mocked in `mockup-hoverfocus.html` (each shown rest → hover → focus):
   - **1 · Soft lift** — hover = a whisper of directional elevation + a hair of brightness; focus = the
     lift + a crisp offset accent ring. No glow, no box at rest.
   - **2 · Crisp edge** — hover = the element's own border crispens to the accent (no fill box); focus =
     a 2px offset accent ring. Flat, sharp.
   - **3 · Hairline + lift** — hover = a tight 1px accent hairline hugging the element + a faint lift;
     focus = hairline + a dotted offset ring (keyboard-distinct).
   → **Iterate a few more with the user, pick ONE language, apply it uniformly.** Constraints: must
   **harmonize with the SHIPPED object-path hover-emphasis** (the `.has-em` / `.em` opacity-dim in
   `report-template.html` — hovering a card both emphasizes its whole flow AND shows the element's own
   hover state, without fighting); the **focus** state must be keyboard-accessible (distinct from hover).
   This is now the primary unresolved visual; lock it before the spec.

## Mockups (on disk — open in a browser or headless-render)

All under `C:\dev\raun\.git\sdd\` (untracked scratch; persist on disk):
- `mockup-toggle.html` — **the locked interaction, live**: hover the fork → block highlight + segmented
  pill; click a segment → graph↔timeline swaps in place. Has a `?theme=light|dark` param + a theme
  button. (Highlight here is the old plain outline; pill grouping is the old ambiguous one — both
  superseded by the OPEN picks above.)
- `mockup-hoverfocus.html` — **the live OPEN item (#2)**: three hover/focus treatments (Soft lift / Crisp
  edge / Hairline + lift) each shown rest → hover → focus, with the REFINED subtle left-right gradients
  applied. Start here next session.
- `mockup-flair.html` — hover-treatment A/B/C/D (glow/outline — now rejected, kept for history) +
  gradient flair + pill grouping variants i/ii (i is locked).
- `mockup-fork-graph.html` / `mockup-options.html` — earlier full-scenario + option-compare mocks (join
  bar A/B/C, icon ideas, chip styles) for reference/history.

Render headless (chromium installed; Playwright MCP defaults to Chrome which is NOT — use the CLI):
`npx playwright screenshot --browser=chromium --full-page --viewport-size=1180,1500 "file:///$(pwd -W)/.git/sdd/mockup-flair.html?theme=dark" out.png`
Open in the user's browser with PowerShell `Start-Process "<abs-path>"`.

## The architectural crux (must be in the spec/plan)

In the timeline cell, `buildForkCell` exposes `it.ports` = each lane's **disc port** (production end),
and `buildObjectFlow` attaches producer→card→consumer edges to those ports. **The graph view relocates
those producer ports** to the bottom of each branch node. So this is a layout **variant**, not just a
second drawing: a fork's view choice changes where producer ports live, and the object-flow pass must
follow. Design the fork renderer to emit the SAME port contract (`it.ports`) from whichever view is
active, so `buildObjectFlow` is view-agnostic. The per-fork toggle then triggers the existing
per-scenario `rerender` (same machinery the collapse-tier click uses) with the fork's view flipped.

Also: graph view changes the fork region's **height/shape** vs the cell, so band-height layout
(`layoutScenario`) must size the Given band from whichever view is active (collapse already reflows via
rerender, so the pattern exists).

## Where we are in the process

Brainstorming (`superpowers:brainstorming`), step "produce mockups / iterate". Remaining:
1. Lock OPEN #1–3 (gradients, hover treatment, pill grouping) — show the user `mockup-flair.html`,
   get picks, optionally one more mock round.
2. Optionally fold the picks back into `mockup-toggle.html` so there's one final "this is it" live demo.
3. Write the design spec → `docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md`
   (cover: graph-view geometry, fork-bar/branch/join layout, the shared `it.ports` contract, the
   toggle interaction + hover highlight + pill, gradient flair, both palettes, the rerender wiring,
   test impact). Self-review, commit (jj), user-review gate.
4. `superpowers:writing-plans` → task plan → `superpowers:subagent-driven-development` to implement.

## Verify loop (for the implementation phase)

`dotnet run --project samples/AppointmentTests -c Debug -- --report-html` (rebuilds Raun.Mtp →
re-embeds the template) → `samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html`.
The **"customer books with parallel arrange"** scenario is the fork test case. Force theme via
`?theme=dark|light` (Playwright CLI defaults to light). Root `*.png`/`*.cjs` scratch is gitignored.
