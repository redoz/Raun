# Fork Graph View + Per-Fork Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render a fork as a standard UML **graph view** (fork bar → content-sized branch nodes → join bar) by default, keep the existing inline-Gantt **timeline cell** as an opt-in alternate, and add a per-fork, hover-revealed **toggle** that swaps a single fork between the two views in place.

**Architecture:** All work is in the single embedded template `src/Raun.Mtp/HtmlReport/report-template.html` (inline HTML/CSS/JS; model injected as JSON at one token). The fork renderer gains a second function `buildForkGraph(it, sc)` that emits the **same `it.ports` contract** as the existing `buildForkCell(it, sc)`, so the object-flow pass (`buildObjectFlow`) stays view-agnostic. A module-scope `adForkView` map (mirroring the existing `adExpansion`) holds the per-fork view choice; `layoutScenario` resolves it onto `it.view` and sizes the fork slot accordingly; `buildActivityDiagram` dispatches on it; the existing per-scenario `rerender()` re-lays-out on toggle. No C# changes.

**Tech Stack:** Hand-authored SVG + vanilla JS inside one HTML file (built with `document.createElementNS` via the `svgEl(tag, attrs)` helper); C#/xUnit tests (`Raun.Mtp.Test`); .NET 10 build; `npx playwright` (chromium) for headless visual verification; `jj` for VCS.

## Global Constraints

Copied verbatim from the spec (`docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md`, §2) — every task implicitly includes these:

- **Self-contained HTML, HARD rule:** inline `<style>`/`<script>` only — **zero** external URLs/CDNs/web-fonts/`@import`. The only allowed literal external string is the SVG namespace `http://www.w3.org/2000/svg`. Source Serif 4 stays base64-embedded.
- **JSON token preserved:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`; `HtmlReportSink` string-replaces it. Don't break it.
- **C# model + builder do NOT change.** `HtmlReportModel` field names are fixed (camelCase serialized). `HtmlReportModelBuilderTests` (the `Verify(json)` snapshot) **must NOT change**. Keep `HtmlReportSinkTests` green. **0-warning build** (`dotnet build Raun.slnx -warnaserror`).
- **Both themes:** define every new CSS var for both `:root` (dark) and `:root[data-theme=light]` (light); verify both.
- **NO decision/merge diamonds** (model is a step-DAG; out of scope, unchanged).
- **VCS: `jj` only** — never `git` mutations. Commit with `jj commit -m "..."`. **No `Co-Authored-By` / tooling trailers** in messages.

## Scope clarifications (read before starting)

1. **This plan = Phase 1 only (graph view + toggle).** The spec also locks a report-wide **sun-driven lighting language** (spec §6–§8). That is an independent subsystem (it re-skins *every* tile, not just forks) and has unresolved multi-SVG concerns the single-SVG mock doesn't cover. It is scoped to a **separate plan** — see [§ Phase 2](#phase-2--sun-driven-lighting-separate-plan-recommended) at the end. Execute Phase 1 first; it is shippable on its own.
2. **The locked mocks are the visual source of truth.** For graph geometry + join-bar option A: `.git/sdd/mockup-toggle.html` (`<svg class="graph">`) and `.git/sdd/mockup-fork-graph.html`. For the toggle interaction (hover → block highlight + segmented pill; click → swap): `.git/sdd/mockup-toggle.html` (live). For pill grouping variant i: `.git/sdd/mockup-flair.html` §2-i. Adapt that static SVG into the model-driven `svgEl` renderer; tune geometry against the mock, don't reinvent it.
3. **Testing strategy.** This repo has **no JS test runner** and the template JS has never been unit-tested (see the shipped diagram's plan, `2026-06-20-report-activity-diagram.md`). So: the **C# observable surface stays green** as a regression guard (`HtmlReportModelBuilderTests` snapshot unchanged — the model is untouched; `HtmlReportSinkTests` substring asserts intact), and each template task ends with a concrete **headless Playwright visual-verify** acceptance against the fixture below. Do **not** add a JS test framework (scope creep, not in the spec).

## The verification fixture (used by every task)

`samples/AppointmentTests`' **"customer books with parallel arrange"** scenario is the fork case (Database-clean → Patient & Slot created on parallel lanes → CreateAppointment reads both + creates Appointment → Then reads Appointment).

```bash
# 1. emit a real report from the sample suite (rebuilds Raun.Mtp -> re-embeds the template)
dotnet run --project samples/AppointmentTests -c Debug -- --report-html
#    -> samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html

# 2. headless-render both themes (chromium is installed; the Playwright MCP defaults to Chrome which is NOT)
R="samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html"
npx playwright screenshot --browser=chromium --full-page --viewport-size=1180,1500 "file://$(pwd)/$R?theme=dark"  out-dark.png
npx playwright screenshot --browser=chromium --full-page --viewport-size=1180,1500 "file://$(pwd)/$R?theme=light" out-light.png
```

Inspect `out-dark.png` / `out-light.png`; compare the fork scenario against the mock. **Headless gotcha:** the Playwright CLI defaults to **light** — always pass `?theme=…` explicitly (a no-param "dark" capture silently renders light). Interaction (hover/click/keyboard) is verified with a short Playwright **driver script** (Node, `.cjs`) — see Task 2. Root `*.png` / `*.cjs` scratch is gitignored.

The full C# suite must stay green every task: `dotnet test` (240/240 baseline) and `dotnet build Raun.slnx -warnaserror` (0 warnings).

---

## File structure

| File | Responsibility | Action |
|---|---|---|
| `src/Raun.Mtp/HtmlReport/report-template.html` | The entire report shell + per-scenario activity-diagram renderer (inline CSS/JS). The **only** production file Phase 1 touches. | **Modify** |

Touched regions inside the template (current line numbers, will drift as you edit):
- CSS: the `/* ACTIVITY DIAGRAM */` block (~L241–255) — add `.ad-fork*` rules.
- JS consts: the `AD_FORK_*` geometry block (~L416) — add `AD_GRAPH_*`.
- JS state: beside `adExpansion`/`expansionFor` (~L402–407) — add `adForkView`/`forkViewFor`/`forkKeyOf`.
- `layoutScenario` (~L495–571) — resolve `it.view`, size the graph slot.
- `buildActivityDiagram` (~L603–709) — wrap forks in `.ad-fork`, dispatch on `it.view`, append the toggle overlay.
- New function `buildForkGraph(it, sc)` — beside `buildForkCell` (~L751).
- New function `buildForkToggle(it)` — beside `buildForkGraph`.
- `buildScenarioCard` (~L1591–1598) — extend the delegated `svg.actdiag` click listener + add keydown for the pill.

---

## Phase 1 — Graph view + per-fork toggle

### Task 1: Graph-view renderer + shared `it.ports` contract (graph becomes the default)

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (consts ~L416; state ~L402; `layoutScenario` ~L516–525; `buildActivityDiagram` dispatch ~L692–693; new `buildForkGraph` near L751)

**Interfaces:**
- Consumes (existing, unchanged): `nodeBox(label)`, `nodeLabel(step)`, `phaseColor(phase)`, `producedResourceType(step, sc)`, `typeColor(type)`, `svgEl(tag, attrs)`, consts `NODE_H`, `AD_W`, `AD_MARGIN`, `AD_BAND_W`, `HALO_R`, `DISC_R`. The existing `buildObjectFlow` reads `it.ports = [{x, y, color, resourceType, stepId}]` and `f.prodItem.kind === "fork"` — **do not change it**.
- Produces: `buildForkGraph(it, sc)` → an SVG `<g>`, and sets `it.ports` with the identical shape `buildForkCell` produces (one entry per lane, disc-port `{x, y, color, resourceType, stepId}`); a resolved `it.view` (`'graph' | 'timeline'`, default `'graph'`) on every fork item; module helpers `forkViewFor(scenarioId)` and `forkKeyOf(stepsArray)`.

- [ ] **Step 1: Add the per-fork view state** beside `adExpansion`/`expansionFor` (~L402). Mirror the collapse-state pattern exactly:

```js
// per-scenario fork view choice: scenarioId -> Map(forkKey -> 'graph'|'timeline'). Module-scope so it
// survives the rerender() replaceChild swap (like adExpansion); session-only, resets on reload.
const adForkView = new Map();
function forkViewFor(scenarioId){
  let m = adForkView.get(scenarioId);
  if (!m){ m = new Map(); adForkView.set(scenarioId, m); }
  return m;
}
// stable fork identity = its member stepIds, sorted (matches the bundle key buildObjectFlow uses).
const forkKeyOf = (steps) => steps.map((s) => s.stepId).sort().join("|");
```

- [ ] **Step 2: Add the graph-view geometry consts** beside `AD_FORK_*` (~L416):

```js
const AD_GRAPH_HEAD = 20, AD_GRAPH_BRANCH_GAP = 16, AD_GRAPH_SIDE_INSET = 12,
      AD_GRAPH_JOIN_GAP = 14, AD_GRAPH_JOIN_H = 10, AD_GRAPH_BAR_H = 5;
```

- [ ] **Step 3: Resolve `it.view` + size the fork slot per view** in `layoutScenario`'s item-sizing map (~L516–525). Replace the `if (g.length >= 2){…}` block with:

```js
if (g.length >= 2){
  const view = forkViewFor(sc.scenarioId).get(forkKeyOf(g)) || "graph";   // graph is the default (G2)
  if (view === "timeline"){
    const w = Math.min(AD_BAND_W - 24, Math.max(160, AD_FORK_W));
    const h = AD_FORK_HEAD + g.length * AD_FORK_ROW + AD_FORK_PAD;
    return { kind: "fork", view, steps: g, phase, w, h, x: cx - w / 2, y: 0 };
  }
  // graph view: fork bar + a row of content-sized branch nodes + join bar
  const lanes = g.slice().sort((a, b) => a.lane - b.lane);
  const branchW = lanes.map((s) => nodeBox(nodeLabel(s)).w);
  const contentW = branchW.reduce((a, b) => a + b, 0) + (lanes.length - 1) * AD_GRAPH_BRANCH_GAP;
  const w = Math.min(AD_BAND_W - 24, Math.max(160, contentW + 2 * AD_GRAPH_SIDE_INSET));
  const h = AD_GRAPH_HEAD + NODE_H + AD_GRAPH_JOIN_GAP + AD_GRAPH_JOIN_H;
  return { kind: "fork", view, steps: g, phase, w, h, x: cx - w / 2, y: 0 };
}
```

- [ ] **Step 4: Dispatch on `it.view`** in `buildActivityDiagram`'s item-body loop (~L692–693). Replace:

```js
for (const it of items)
  svg.appendChild(it.kind === "fork"
    ? (it.view === "timeline" ? buildForkCell(it, sc) : buildForkGraph(it, sc))
    : actionNode(it));
```

- [ ] **Step 5: Implement `buildForkGraph(it, sc)`** as a new function beside `buildForkCell` (~L751). It emits the same `it.ports` contract so `buildObjectFlow` is unchanged:

```js
// Graph view of a fork (spec §4): a short spine stub into a solid slate FORK BAR, a row of content-sized
// phase-tinted BRANCH NODES (one per lane, click->focusStep via data-step), producer DISC PORTS at each
// branch-node bottom, then an option-A slate JOIN BAR segmented with a gap at each producing disc x, and a
// spine stub out. Sets it.ports identically to buildForkCell so buildObjectFlow attaches edges view-agnostically.
function buildForkGraph(it, sc){
  const g = svgEl("g");
  const GAP = 5, cx = AD_W / 2, tint = phaseColor(it.phase);
  const lanes = it.steps.slice().sort((a, b) => a.lane - b.lane);
  const forkBarY = it.y + AD_GRAPH_HEAD - AD_GRAPH_BAR_H;
  const branchTop = it.y + AD_GRAPH_HEAD, branchBot = branchTop + NODE_H;
  const joinY = branchBot + AD_GRAPH_JOIN_GAP;

  // central spine stubs so the control edge visibly meets the bars (the edge from buildActivityDiagram
  // ends at it.y and resumes at it.y+it.h; bridge to the fork/join bars).
  const spine = svgEl("g", { stroke: "var(--ad-control)", "stroke-width": "1", fill: "none" });
  spine.appendChild(svgEl("line", { x1: cx, y1: it.y, x2: cx, y2: forkBarY }));
  spine.appendChild(svgEl("line", { x1: cx, y1: joinY + AD_GRAPH_BAR_H, x2: cx, y2: it.y + it.h }));
  g.appendChild(spine);

  // PARALLEL tag
  const tag = svgEl("text", { class: "ad-tk", x: it.x + 2, y: it.y + 7 });
  tag.style.letterSpacing = ".12em"; tag.textContent = "PARALLEL";
  g.appendChild(tag);

  // fork bar (top, full slot width, slate)
  const fork = svgEl("rect", { x: it.x, y: forkBarY, width: it.w, height: AD_GRAPH_BAR_H, rx: 2.5 });
  fork.style.fill = "var(--ad-wall)";
  g.appendChild(fork);

  // branch nodes, content-sized, centred on the spine; collect each lane's producer disc port
  const boxes = lanes.map((s) => Object.assign({ s }, nodeBox(nodeLabel(s))));
  const totalW = boxes.reduce((a, b) => a + b.w, 0) + (boxes.length - 1) * AD_GRAPH_BRANCH_GAP;
  let bx = cx - totalW / 2;
  const ports = [];
  for (const b of boxes){
    const s = b.s, status = (s.status || "").toLowerCase();
    const node = svgEl("g");
    node.setAttribute("data-step", s.stepId);                 // click -> focusStep delegation (existing)
    const rect = svgEl("rect", { x: bx, y: branchTop, width: b.w, height: NODE_H, rx: 3 });
    if (status === "skipped"){
      rect.style.fill = "color-mix(in srgb, var(--ad-grey) 12%, var(--ad-panel))";
      rect.style.stroke = "var(--ad-grey)"; rect.setAttribute("stroke-width", "1");
    } else if (status === "failed"){
      rect.style.fill = "color-mix(in srgb, " + tint + " 15%, var(--ad-panel))";
      rect.style.stroke = "var(--fail)"; rect.setAttribute("stroke-width", "1.3");
    } else {
      rect.style.fill = "color-mix(in srgb, " + tint + " 15%, var(--ad-panel))";
      rect.style.stroke = "color-mix(in srgb, " + tint + " 32%, transparent)"; rect.setAttribute("stroke-width", "1");
    }
    node.appendChild(rect);
    const label = svgEl("text", {
      class: "ad-nm", x: bx + b.w / 2, y: branchTop + NODE_H / 2,
      "text-anchor": "middle", "dominant-baseline": "central",
    });
    if (status === "skipped") label.style.fill = "var(--ad-grey)";
    label.textContent = nodeLabel(s);
    node.appendChild(label);
    g.appendChild(node);

    const rtype = producedResourceType(s, sc);
    ports.push({
      x: bx + b.w / 2, y: branchBot,
      color: rtype ? typeColor(rtype) : "var(--ad-control)", resourceType: rtype, stepId: s.stepId,
    });
    bx += b.w + AD_GRAPH_BRANCH_GAP;
  }

  // join bar (option A): slate, segmented with a GAP at each producing disc x (mirrors buildForkCell)
  const gapXs = ports.filter((p) => p.resourceType).map((p) => p.x).sort((a, b) => a - b);
  const join = svgEl("g"); join.style.fill = "var(--ad-wall)";
  let segX = it.x;
  for (const gx of gapXs){
    const gs = gx - GAP / 2;
    if (gs > segX) join.appendChild(svgEl("rect", { x: segX, y: joinY, width: gs - segX, height: AD_GRAPH_BAR_H }));
    segX = gx + GAP / 2;
  }
  if (it.x + it.w > segX) join.appendChild(svgEl("rect", { x: segX, y: joinY, width: it.x + it.w - segX, height: AD_GRAPH_BAR_H }));
  g.appendChild(join);

  // disc ports (filled disc + faint halo) drawn on top
  for (const p of ports){
    if (!p.resourceType) continue;
    const halo = svgEl("circle", { cx: p.x, cy: p.y, r: HALO_R, fill: "none", "stroke-width": "0.8" });
    halo.style.stroke = p.color; halo.style.opacity = ".35"; g.appendChild(halo);
    const disc = svgEl("circle", { cx: p.x, cy: p.y, r: DISC_R }); disc.style.fill = p.color; g.appendChild(disc);
  }

  it.ports = ports;   // the contract buildObjectFlow consumes (same shape as the timeline cell)
  return g;
}
```

- [ ] **Step 6: Re-emit the report and verify the graph view renders by default**

Run the fixture (both themes). Expected in the fork scenario:
- fork bar (slate) → two branch nodes (`patient Jane exists`, `an available slot exists`, content-sized, phase-tinted) → join bar; the control spine enters the fork bar and leaves the join bar.
- **object flow intact (the contract proof):** each branch's producer disc → an entity card below the fork → consumer edges into `creating an appointment`; the produce-edge verb label (`create`) renders via the unchanged `placeVerbLabels` (no change needed — it labels edges, not views).
- both themes correct; no console errors.

- [ ] **Step 7: Guard the C# surface** — `dotnet test` (240/240 green; `HtmlReportModelBuilderTests` snapshot unchanged because the model is untouched) and `dotnet build Raun.slnx -warnaserror` (0 warnings).

- [ ] **Step 8: Commit**

```bash
jj commit -m "report: fork graph view (fork bar -> branch nodes -> join bar) as default; shared it.ports contract"
```

---

### Task 2: Per-fork toggle — hover highlight + segmented pill + rerender wiring

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (CSS ~L255; `buildActivityDiagram` fork append ~L692; new `buildForkToggle` near `buildForkGraph`; `buildScenarioCard` click listener ~L1591–1598)

**Interfaces:**
- Consumes: `forkViewFor(scenarioId)`, `forkKeyOf(steps)`, `it.view` (Task 1); `measureText(str, px, weight)`; the per-scenario `rerender` closure (passed into `buildActivityDiagram` → reachable in `buildScenarioCard`); `AD_W`.
- Produces: a `.ad-fork` wrapper group per fork carrying `data-fork-key`; `buildForkToggle(it)` → an SVG `<g>` holding `.ad-fork-hl` (block highlight) + `.ad-fork-pill` (segmented `show as [ graph | timeline ]`, segments tagged `data-fork-set="graph|timeline"`, `role="button"`, `tabindex="0"`).

- [ ] **Step 1: Wrap forks in `.ad-fork` and append the toggle overlay** in `buildActivityDiagram` (~L692). Replace the Task-1 dispatch loop with:

```js
for (const it of items){
  if (it.kind !== "fork"){ svg.appendChild(actionNode(it)); continue; }
  const wrap = svgEl("g", { class: "ad-fork" });
  wrap.setAttribute("data-fork-key", forkKeyOf(it.steps));
  wrap.appendChild(it.view === "timeline" ? buildForkCell(it, sc) : buildForkGraph(it, sc));
  wrap.appendChild(buildForkToggle(it));    // hover-revealed highlight + pill (drawn once, both views)
  svg.appendChild(wrap);
}
```

- [ ] **Step 2: Implement `buildForkToggle(it)`** beside `buildForkGraph`. SVG-native (positions correctly inside the per-scenario actdiag SVG; self-contained). Geometry tracks `.git/sdd/mockup-toggle.html` — tune in Step 6:

```js
// Hover/focus-revealed per-fork toggle: a faint accent block highlight + a near-square segmented pill
// reading "show as [ graph | timeline ]" (variant i — muted label, then a bordered track of two segments;
// active segment highlighted). Reveal/hide is pure CSS (.ad-fork:hover/:focus-within). Clicking/Enter on
// a segment is handled by the delegated listener in buildScenarioCard.
function buildForkToggle(it){
  const g = svgEl("g"), cx = AD_W / 2;

  // block highlight: wraps the whole fork slot, behind the pill
  const hl = svgEl("rect", {
    class: "ad-fork-hl", x: it.x - 4, y: it.y - 4, width: it.w + 8, height: it.h + 8, rx: 6,
    fill: "var(--accent)", "fill-opacity": ".06", stroke: "var(--accent)", "stroke-opacity": ".5", "stroke-width": "1.2",
  });
  g.appendChild(hl);

  // segmented pill, centred over the fork bar
  const FS = 8.5, H = 16, SEG_PADX = 9, RX = 4, current = it.view === "timeline" ? "timeline" : "graph";
  const labW = measureText("show as", FS, 400) + 8;
  const segs = ["graph", "timeline"].map((t) => ({ t, w: Math.round(measureText(t, FS, 500) + 2 * SEG_PADX) }));
  const trackW = segs.reduce((a, s) => a + s.w, 0) + 4;          // +inner pad
  const pillW = 4 + labW + trackW + 6;
  const px = Math.round(cx - pillW / 2), py = Math.round(it.y + 4);

  const pill = svgEl("g", { class: "ad-fork-pill" });
  const bg = svgEl("rect", { x: px, y: py, width: pillW, height: H, rx: RX });
  bg.style.fill = "var(--ad-panel)"; bg.style.stroke = "var(--ad-card-border)"; bg.setAttribute("stroke-width", "1");
  bg.style.filter = "drop-shadow(0 2px 6px rgba(0,0,0,.30))";
  pill.appendChild(bg);

  const lab = svgEl("text", { x: px + 4 + labW / 2, y: py + H / 2, "text-anchor": "middle", "dominant-baseline": "central" });
  lab.style.cssText = "font-size:" + FS + "px;fill:var(--muted);";
  lab.textContent = "show as";
  pill.appendChild(lab);

  const trackX = px + 4 + labW, trackY = py + 2.5;
  const track = svgEl("rect", { x: trackX, y: trackY, width: trackW, height: H - 5, rx: RX - 1 });
  track.style.fill = "var(--ad-cell)"; track.style.stroke = "var(--ad-card-border)"; track.setAttribute("stroke-width", "1");
  pill.appendChild(track);

  let sx = trackX + 2;
  for (const s of segs){
    const active = s.t === current;
    const seg = svgEl("g", { class: "ad-fork-seg", role: "button", tabindex: "0" });
    seg.setAttribute("data-fork-set", s.t);
    seg.setAttribute("aria-label", "show this fork as " + s.t);
    const box = svgEl("rect", { x: sx, y: trackY + 1.5, width: s.w, height: H - 8, rx: RX - 2 });
    box.style.fill = active ? "var(--accent)" : "transparent";
    seg.appendChild(box);
    const tx = svgEl("text", { x: sx + s.w / 2, y: py + H / 2, "text-anchor": "middle", "dominant-baseline": "central" });
    tx.style.cssText = "font-size:" + FS + "px;font-weight:500;fill:" + (active ? "#fff" : "var(--muted)") + ";";
    tx.textContent = s.t;
    seg.appendChild(tx);
    pill.appendChild(seg);
    sx += s.w;
  }
  g.appendChild(pill);
  return g;
}
```

- [ ] **Step 3: Add the CSS** after the `.actdiag [data-step]` rule (~L255):

```css
/* per-fork toggle: zero chrome at rest; revealed on hover or keyboard focus-within */
.ad-fork .ad-fork-hl, .ad-fork .ad-fork-pill{ opacity:0; transition:opacity .12s; pointer-events:none; }
.ad-fork:hover .ad-fork-hl, .ad-fork:focus-within .ad-fork-hl,
.ad-fork:hover .ad-fork-pill, .ad-fork:focus-within .ad-fork-pill{ opacity:1; }
.ad-fork:hover .ad-fork-pill, .ad-fork:focus-within .ad-fork-pill{ pointer-events:auto; }
.ad-fork-seg{ cursor:pointer; }
.ad-fork-seg:focus-visible{ outline:2px solid var(--ring, var(--accent)); outline-offset:2px; border-radius:3px; }
@media (prefers-reduced-motion: reduce){ .ad-fork .ad-fork-hl, .ad-fork .ad-fork-pill{ transition:none; } }
```

- [ ] **Step 4: Wire activation** in `buildScenarioCard` — extend the existing delegated `diagramSvg` click listener (~L1593) and add a keydown sibling. The pill segment check must run **before** the `[data-step]` focus-step path and `return` so a toggle click never also focuses a step:

```js
const onForkSet = (target) => {
  const seg = target && target.closest ? target.closest("[data-fork-set]") : null;
  if (!seg) return false;
  const fork = seg.closest(".ad-fork");
  if (fork){
    forkViewFor(sc.scenarioId).set(fork.getAttribute("data-fork-key"), seg.getAttribute("data-fork-set"));
    rerender();                       // same machinery the collapse-tier click uses
  }
  return true;
};
if (diagramSvg){
  diagramSvg.addEventListener("click", (ev) => {
    if (onForkSet(ev.target)) return;
    const target = ev.target && ev.target.closest ? ev.target.closest("[data-step]") : null;
    if (!target) return;
    focusStep(target.dataset.step, { toggle: true });
  });
  diagramSvg.addEventListener("keydown", (ev) => {
    if (ev.key !== "Enter" && ev.key !== " ") return;
    if (onForkSet(ev.target)){ ev.preventDefault(); }
  });
}
```

(Replace the existing single click listener at ~L1593 with this block — keep `focusStep` as defined.)

- [ ] **Step 5: Re-emit and verify the toggle (static screenshots first)** — run the fixture both themes. At rest: zero toggle chrome (no highlight, no pill). The default fork still renders as the graph view (Task 1).

- [ ] **Step 6: Verify interaction with a Playwright driver** — write a throwaway `drive.cjs` (gitignored root scratch) that loads the report, hovers the fork group, screenshots (expect block highlight + `show as [ graph | timeline ]` pill with `graph` active), clicks the `timeline` segment, screenshots (expect the **same fork** now the timeline cell, object flows still attached — the contract proof across the swap), then `Tab` to a segment + `Enter` and confirm it switches. Pattern:

```js
const { chromium } = require("playwright");
(async () => {
  const b = await chromium.launch(); const p = await b.newPage({ viewport: { width: 1180, height: 1500 } });
  const url = "file://" + process.cwd().replace(/\\/g, "/") +
    "/samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html?theme=dark";
  await p.goto(url);
  const fork = p.locator(".ad-fork").first();
  await fork.hover();                              await p.screenshot({ path: "tg-hover.png" });
  await p.locator('[data-fork-set="timeline"]').first().click();
  await fork.hover();                              await p.screenshot({ path: "tg-timeline.png" });
  await p.locator('[data-fork-set="graph"]').first().click();
  await p.screenshot({ path: "tg-graph.png" });
  await b.close();
})();
```
Run: `node drive.cjs`. Inspect `tg-hover.png` / `tg-timeline.png` / `tg-graph.png` against `.git/sdd/mockup-toggle.html`. Tune pill geometry (Step 2) if it crowds the fork bar. Re-run both themes.

- [ ] **Step 7: Regression guard** — `dotnet test` (240/240) + `dotnet build Raun.slnx -warnaserror` (0 warnings). Confirm a non-fork scenario is unaffected and (if any) a second fork toggles independently of the first (per-fork key).

- [ ] **Step 8: Commit**

```bash
jj commit -m "report: per-fork graph/timeline toggle (hover highlight + segmented pill, keyboard-operable)"
```

---

## Phase 1 self-review (run before handing off)

1. **Spec coverage (§4/§5/§6, the fork-specific half):** graph view as default (Task 1 §3/§5) ✓; timeline opt-in via toggle (Task 1 dispatch + Task 2) ✓; one `it.ports` contract, two renderers (Task 1 §5) ✓; per-fork session-only state mirroring `adExpansion` (Task 1 §1) ✓; hover highlight + variant-i pill + click-swap + keyboard (Task 2) ✓; join-bar option A (Task 1 §5 `gapXs`) ✓; `rerender` reuse (Task 2 §4) ✓; both palettes (every new var/color is `var(--…)` or `color-mix` over existing palette tokens) ✓. **Not in Phase 1:** the lighting/gradient/focus-bracket language (§6–§8) → Phase 2.
2. **Placeholder scan:** every code step carries real code; the only "tune against the mock" notes are pill geometry (Task 2 §6) — concrete starting values given, mock is the reference (house style, scope clarification #2). No `TODO`/`TBD`/"add error handling".
3. **Type/name consistency:** `forkViewFor`/`forkKeyOf`/`it.view`/`it.ports`/`adForkView`/`buildForkGraph`/`buildForkToggle`/`data-fork-key`/`data-fork-set`/`.ad-fork`/`.ad-fork-hl`/`.ad-fork-pill`/`.ad-fork-seg` are used identically across Tasks 1–2. `it.ports` entry shape matches `buildForkCell`'s exactly.

---

## Phase 2 — Sun-driven lighting (separate plan recommended)

Spec §6–§8 lock a report-wide **single-light "sun" lighting language** (locked mock `.git/sdd/mockup-hover-sheen.html`): per-tile `userSpaceOnUse` directional gradients (whisper-subtle fill, lit border, faint gloss), a cursor-tracked surface **sheen** + far-edge rim **glint** revealed on hover, a **time-of-day sun** driving angle + a `--sun` intensity multiplier on a sinusoid, an eased `requestAnimationFrame` loop, **corner-bracket** keyboard focus, and `prefers-reduced-motion` fallback.

**Why it's a separate plan (writing-plans scope-check):** it is **independent of Phase 1** — it re-skins *every* interactive tile (`actionNode`, `cardEl`/`stackCardEl`/`greyCardEl`, the fork/join bars in both fork views), not just forks, and Phase 1 ships without it. It is large enough to warrant its own task-by-task plan, and it has **open design questions the single-SVG mock does not resolve** (below) that must be settled before it can be planned to this plan's concrete, no-placeholder depth.

**Open implementation concerns to resolve first (the mock is one SVG; the report is N per-scenario SVGs):**
1. **Per-SVG gradient instances.** The shared `#sheen`/`#edge` and every per-tile gradient are `userSpaceOnUse` — tied to a coordinate space. The report renders one `<svg class="actdiag">` per scenario, each with its own viewBox. A single global gradient cannot serve all of them. Resolution likely: per-scenario gradient instances (id suffixed by `scenarioId`, appended to that SVG's existing `<defs>` at ~L623), and a registry that knows which SVG each belongs to.
2. **Cursor → which-SVG mapping.** The mock's `pointermove` uses one `getScreenCTM`. The report must detect which scenario SVG the pointer is over and transform into *that* SVG's user space (or light only the hovered SVG; others rest at sun-only).
3. **Rerender pruning.** `rerender()` rebuilds one scenario card, detaching its gradient nodes. The global rAF loop's registry must drop detached entries (`el.isConnected` filter) so it doesn't leak or write to dead nodes — the same "stale-entry per re-render" class noted as a deferred minor in the shipped diagram.
4. **Performance at report scale.** The mock guards its rAF (only rewrites when the eased direction changes `> 0.0008`). With many scenarios/tiles, consider decoupling the slow sun clock (per-tile fills/borders) from the fast cursor track (only the 2 shared sheen/edge gradients) — the spec §9 already flags this as an allowed optimization.
5. **Interaction with Phase 1's pill.** The active pill segment should pick up the lighting (spec G5) and the segments take the bracket focus (spec §7.5) — a small integration point once both exist.

**Anticipated Phase 2 task shape** (to be detailed in its own plan once 1–5 are resolved): (a) theme vars + reduced-motion CSS + per-scenario shared `#sheen`/`#edge` defs + the per-instance gradient registry; (b) lit fill/border/gloss on nodes + branch nodes; (c) same on cards + fork/join bars + fork block; (d) the sun (time-of-day → anchor + `--sun`, real local clock, slow interval); (e) the eased rAF loop + cursor tracking + isConnected pruning + reduced-motion skip; (f) hover sheen + rim glint overlays (coexisting with the shipped `.has-em` path-emphasis); (g) corner-bracket `:focus-visible` + `tabindex`/`role`/`aria` on all tiles + keydown parity.

**Recommendation:** ship Phase 1, then write `docs/superpowers/plans/2026-06-21-report-sun-lighting.md` resolving the five concerns above. (If you'd rather I draft that plan now with explicit assumptions for the open concerns, say so — but Phase 1 is the cleaner first PR.)

---

## Execution Handoff

Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks (REQUIRED SUB-SKILL: superpowers:subagent-driven-development).
2. **Inline Execution** — execute tasks in this session with checkpoints (REQUIRED SUB-SKILL: superpowers:executing-plans).

Either way: the verify loop (headless both-theme render + the Task 2 driver script + `dotnet test` / `-warnaserror`) is the per-task green gate. `jj`-only, no trailers.
