# Report-wide Sun-Driven Lighting Implementation Plan (Phase 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Skin every interactive tile of the per-scenario activity diagrams with one "sun" light — a directional per-tile fill/border/gloss driven by the local time of day, a cursor-tracked surface sheen + far-edge rim glint revealed on hover in the SVG the pointer is over, and uniform corner-bracket keyboard focus — across the report's N independent `<svg class="actdiag">` diagrams.

**Architecture:** All work is in the single embedded template `src/Raun.Mtp/HtmlReport/report-template.html`. Two decoupled paths (spec §3): a **slow global sun** (`applySun()` on a ~60s clock + at build time) re-aims every per-tile `userSpaceOnUse` gradient via `sunDir`; a **fast local cursor** (`requestAnimationFrame`, only while hovering) positions just the hovered SVG's 2 shared gradients (`#sheen-<id>`/`#edge-<id>`). Per-tile gradients follow **only** the sun (decision D2) so the fast path never rewrites more than 2 nodes/frame and rerender needs no per-tile registry (tiles are born sun-lit at build time, re-found from live DOM each clock tick). No C# changes.

**Tech Stack:** Hand-authored SVG + vanilla JS inside one HTML file (`document.createElementNS` via `svgEl`); C#/xUnit tests (`Raun.Mtp.Test`); .NET 10 build; `npx playwright` (chromium) for headless visual verification; `jj` for VCS.

## Global Constraints

Copied verbatim from the spec (`docs/superpowers/specs/2026-06-21-report-sun-lighting-design.md` §2 and `docs/superpowers/specs/2026-06-21-fork-graph-view-toggle-design.md` §6–§8) — every task implicitly includes these:

- **Self-contained HTML, HARD rule:** inline `<style>`/`<script>` only — **zero** external URLs/CDNs/web-fonts/`@import`. The only allowed literal external string is the SVG namespace `http://www.w3.org/2000/svg`. Source Serif 4 stays base64-embedded. Time-of-day uses the **client's local clock** (`new Date()`) — no network.
- **JSON token preserved:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`; don't break it.
- **C# model + builder do NOT change.** `HtmlReportModelBuilderTests` (the `Verify(json)` snapshot) **must NOT change**. Keep `HtmlReportSinkTests` green. **0-warning build** (`dotnet build Raun.slnx -warnaserror`).
- **Both themes:** define every new CSS var for both light blocks (`:root` default + `:root[data-theme="light"]`) and both dark blocks (`@media (prefers-color-scheme: dark) :root` + `:root[data-theme="dark"]`); verify both.
- **NO decision/merge diamonds** (out of scope, unchanged).
- **Locked visual authority:** `.git/sdd/mockup-hover-sheen.html`. Tune geometry/opacity against it; don't reinvent. Sun math/easing/gradient/focus detail = fork-graph spec §7.
- **VCS: `jj` only** — never `git` mutations. Commit with `jj commit -m "..."`. **No `Co-Authored-By` / tooling trailers.**

## Scope clarifications (read before starting)

1. **Decisions locked in the design (do not re-litigate):** **D1** global sun + hovered-SVG cursor; **D2** per-tile gradients follow only the sun (cursor drives sheen+glint only — no per-tile angle nudge); **D3** uniform corner-bracket focus on every tile incl. the Phase-1 pill segments.
2. **What gets the full tile treatment** (lit fill/border/gloss + hover sheen/glint + brackets + `tabindex/role/aria`): action/assert **nodes**, graph **branch nodes**, entity **cards** (incl. stack/grey collapsed cards), and the **timeline cell panel** (the "fork block"). **What gets lit fill only** (a directional gradient, no sheen/glint/brackets/focus — they are structural, not interactive): the graph **fork/join bars**, the timeline **fork/join/side walls**, and the **active pill segment** (G5). **What stays flat:** the timeline's inner Gantt lane bars, gridlines, ports, spine, initial/final nodes (data/structure, not tiles — matches the mock, which has no Gantt).
3. **Testing strategy.** This repo has **no JS test runner** (same as the shipped diagram + Phase 1). Regression guard = the C# observable surface stays green (`HtmlReportModelBuilderTests` snapshot unchanged — the model is untouched; `HtmlReportSinkTests` substring asserts intact). Each template task ends with a concrete **headless Playwright visual-verify** against the fixture. Do **not** add a JS test framework (scope creep, not in the spec).

## The verification fixture (used by every task)

`samples/AppointmentTests`' **"customer books with parallel arrange"** scenario is the fork case; the suite also has non-fork scenarios (the multi-SVG proof).

```bash
# 1. emit a real report (rebuilds Raun.Mtp -> re-embeds the template)
dotnet run --project samples/AppointmentTests -c Debug -- --report-html
#    -> samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html

# 2. headless-render both themes (chromium is installed; the Playwright MCP defaults to Chrome which is NOT)
R="samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html"
npx playwright screenshot --browser=chromium --full-page --viewport-size=1180,1600 "file://$(pwd)/$R?theme=dark"  out-dark.png
npx playwright screenshot --browser=chromium --full-page --viewport-size=1180,1600 "file://$(pwd)/$R?theme=light" out-light.png
```

**Headless gotcha:** the Playwright CLI defaults to **light** — always pass `?theme=…` explicitly. The cursor-tracked sheen/glint and the which-SVG mapping need a Playwright **driver script** (Node `.cjs`, `getScreenCTM` mouse moves) — see Task 3. To exercise a specific time of day deterministically, the driver can stub the clock (see Task 3 Step 5). Root `*.png` / `*.cjs` scratch is gitignored.

The full C# suite must stay green every task: `dotnet test Raun.slnx -c Debug` (240/240 baseline; do **not** pass `--nologo` — Raun is an MTP framework and rejects it) and `dotnet build Raun.slnx -warnaserror` (0 warnings).

---

## File structure

| File | Responsibility | Action |
|---|---|---|
| `src/Raun.Mtp/HtmlReport/report-template.html` | The entire report shell + per-scenario activity-diagram renderer (inline CSS/JS). The **only** production file Phase 2 touches. | **Modify** |

Touched regions inside the template (current line numbers, will drift as you edit):
- CSS palettes: `:root` universal (~L17), light default `:root` (~L20–39), `@media dark :root` (~L42–61), `:root[data-theme="light"]` (~L64–82), `:root[data-theme="dark"]` (~L84–102).
- CSS activity-diagram block end (~L255–263) — add lighting/stop/reduced-motion rules.
- JS consts + module state (~L426–446) — add the sun engine + helpers near here.
- `buildActivityDiagram` defs (~L651–662) — add per-scenario `#sheen-<id>`/`#edge-<id>`; node dispatch (~L723).
- `actionNode` (~L749), `buildForkGraph` branch nodes + bars (~L872–915), `cardEl`/`stackCardEl`/`greyCardEl` (~L1618–1692), `buildForkCell` walls (~L1037–1068), `buildForkToggle` active segment (~L824–826).
- Bootstrap (~L619–620) — `applySun()` + interval; `renderScenario`/`buildScenarioCard` (~L1721–1806) — cursor path + keydown parity.

---

## Task 1: Sun engine + lit action/branch nodes

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (CSS palettes ~L17–102; CSS block end ~L255; sun engine near consts ~L446; `buildActivityDiagram` defs ~L651 + node dispatch ~L723; `actionNode` ~L749; `buildForkGraph` branch-node rect ~L876–887)

**Interfaces:**
- Consumes (existing, unchanged): `svgEl(tag, attrs)`, `nodeLabel(step)`, `phaseColor(phase)`, `AD_W`, `NODE_H`, consts; `renderAll()`, `document.fonts.ready`.
- Produces (later tasks rely on these exact names): module `sunDir = {dx, dy}` (unit light direction), `AD_SUN_R`, `AD_SCENE = {x, y}`, `reduceMotion` (bool); `dirGrad(stops, cx, cy, L) -> [id, <linearGradient class="ad-dir">]` (stops = `[{o, c, op?}]`); `brackets(x, y, w, h) -> <g>` (corner reticle, `.brk` paths); `litTile(o) -> <g class="ad-lit">` where `o = {x, y, w, h, rx, sid, sw?, fill:[stops], border:[stops], gloss?:bool, label, body?:(g)=>void}`; `applySun()`; per-scenario `#sheen-<scenarioId>` (`radialGradient.ad-sheen`) + `#edge-<scenarioId>` (`linearGradient.ad-edge`) in each SVG's `<defs>`; `actionNode(it, sid)` (now takes `sid`).

- [ ] **Step 1: Add the lighting theme vars.** In the universal `:root` at ~L17, replace:

```css
:root{ --ad-font:'Source Serif 4', Georgia, serif; }
```

with (constant, theme-independent vars; `--sun` is overwritten by JS):

```css
:root{ --ad-font:'Source Serif 4', Georgia, serif; --bright:1.03; --sun:1; --ring:var(--accent); }
```

Then add this one line to the **end** of each of the four palette blocks (just before the closing `}` of each): to both **light** blocks (the default `:root` ~L38 and `:root[data-theme="light"]` ~L82):

```css
    --sheen:#bcd4ff; --sheen-peak:.14; --edge:#ffffff; --edge-peak:.48;
```

and to both **dark** blocks (the `@media (prefers-color-scheme: dark) :root` ~L60 and `:root[data-theme="dark"]` ~L102):

```css
    --sheen:#ffffff; --sheen-peak:.06; --edge:#ffffff; --edge-peak:.5;
```

- [ ] **Step 2: Add the lighting CSS** immediately before the `@media (prefers-reduced-motion: reduce){ .ad-fork … }` line (~L263, inside the activity-diagram block):

```css
  /* sun-driven lighting (Phase 2). Per-instance gradient stops keyed by CLASS so one rule serves all N svgs. */
  .ad-sheen .ss0{ stop-color:var(--sheen); stop-opacity:calc(var(--sheen-peak) * var(--sun)); }
  .ad-sheen .ss1{ stop-color:var(--sheen); stop-opacity:calc(var(--sheen-peak) * var(--sun) * 0.3); }
  .ad-sheen .ss2{ stop-color:var(--sheen); stop-opacity:0; }
  .ad-edge  .es0{ stop-color:var(--edge);  stop-opacity:0; }
  .ad-edge  .es2{ stop-color:var(--edge);  stop-opacity:calc(var(--edge-peak) * var(--sun)); }
  /* hover sheen + rim glint: hidden at rest, gentle fade (quicker in, slower out). Positioned by the rAF (Task 3). */
  .ad-lit .ad-sh, .ad-lit .ad-gl{ opacity:0; transition:opacity .5s ease; pointer-events:none; }
  .ad-lit:hover .ad-sh, .ad-lit:hover .ad-gl{ opacity:1; transition:opacity .22s ease; }
  .ad-lit{ cursor:pointer; }
  .ad-lit:focus{ outline:none; }
  /* corner-bracket keyboard focus (distinct from hover); revealed only on :focus-visible (Task 4 extends parity). */
  .ad-lit .brk{ opacity:0; transition:opacity .14s ease; pointer-events:none; }
  .ad-lit:focus-visible .brk{ opacity:1; }
  @media (prefers-reduced-motion: reduce){
    .ad-lit .ad-sh, .ad-lit .ad-gl{ display:none; }       /* no cursor-driven effects without motion */
    .ad-lit .ad-sh, .ad-lit .ad-gl, .ad-lit .brk{ transition:none; }
  }
```

- [ ] **Step 3: Add the sun engine + helpers.** Insert after the `phaseColor` const (~L446), before the collapse-tier section:

```js
    // ---- sun-driven lighting (Phase 2, spec §3/§7) ------------------------
    // One light = the sun for the LOCAL time of day. sunDir is the unit direction tiles are lit FROM
    // (scene-centre -> away from the sun). applySun() refreshes it + the --sun intensity on a slow clock
    // and re-aims every per-tile gradient. The cursor fast-path (sheen/glint) lives in Task 3.
    const AD_SUN_R = 2400;                       // anchor distance >> diagram, so the angle is ~constant across tiles
    const AD_SCENE = { x: AD_W / 2, y: 200 };    // nominal scene centre used as the sun-anchor reference
    const reduceMotion = !!(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches);
    let sunDir = { dx: 0.6, dy: -0.8 };          // pleasant upper-left default until applySun() runs

    // a per-instance directional gradient: userSpaceOnUse, FIXED length L=max(w,h), centred on (cx,cy),
    // endpoints aimed along the current sunDir. Carries data-cx/cy/l so applySun() can re-aim it with NO
    // stored reference. stops = [{o, c, op?}]. Returns [id, <linearGradient>] — the caller appends the element.
    let _adg = 0;
    function dirGrad(stops, cx, cy, L){
      const id = "adg" + (_adg++), h = L / 2;
      const grad = svgEl("linearGradient", {
        id, class: "ad-dir", gradientUnits: "userSpaceOnUse",
        "data-cx": cx.toFixed(2), "data-cy": cy.toFixed(2), "data-l": L.toFixed(2),
        x1: (cx - sunDir.dx * h).toFixed(2), y1: (cy - sunDir.dy * h).toFixed(2),
        x2: (cx + sunDir.dx * h).toFixed(2), y2: (cy + sunDir.dy * h).toFixed(2),
      });
      for (const s of stops){
        const st = svgEl("stop", { offset: s.o });
        st.style.stopColor = s.c;
        if (s.op != null) st.style.stopOpacity = s.op;
        grad.appendChild(st);
      }
      return [id, grad];
    }

    // four thin accent corner ticks hugging a tile (a reticle) — the keyboard-focus affordance (spec §7.5).
    function brackets(x, y, w, h){
      const L = 5.4, o = 1.7, sw = 0.85, g = svgEl("g", { class: "brk" });
      const seg = (d) => {
        const p = svgEl("path", { d, fill: "none", "stroke-width": sw, "stroke-linecap": "butt", "stroke-linejoin": "miter" });
        p.style.stroke = "var(--ring)"; g.appendChild(p);
      };
      seg("M" + (x - o) + "," + (y + L) + " V" + (y - o + 1) + " Q" + (x - o) + "," + (y - o) + " " + (x - o + 1) + "," + (y - o) + " H" + (x + L));
      seg("M" + (x + w - L) + "," + (y - o) + " H" + (x + w + o - 1) + " Q" + (x + w + o) + "," + (y - o) + " " + (x + w + o) + "," + (y - o + 1) + " V" + (y + L));
      seg("M" + (x + w + o) + "," + (y + h - L) + " V" + (y + h + o - 1) + " Q" + (x + w + o) + "," + (y + h + o) + " " + (x + w + o - 1) + "," + (y + h + o) + " H" + (x + w - L));
      seg("M" + (x + L) + "," + (y + h + o) + " H" + (x - o + 1) + " Q" + (x - o) + "," + (y + h + o) + " " + (x - o) + "," + (y + h + o - 1) + " V" + (y + h - L));
      return g;
    }

    // Wrap a tile in the full sun-lit treatment: directional fill + border + gloss gradients (aimed by sunDir),
    // a hover sheen + far-edge rim-glint overlay (per-svg #sheen-<sid>/#edge-<sid>, revealed on :hover by CSS,
    // positioned by the Task-3 rAF), and a corner-bracket :focus-visible reticle. The caller draws label/header
    // on top via o.body(g). o = {x,y,w,h,rx,sid,sw?,fill,border,gloss?,label,body?}.
    function litTile(o){
      const g = svgEl("g", { class: "ad-lit", tabindex: "0", role: "button" });
      if (o.label) g.setAttribute("aria-label", o.label);
      const cx = o.x + o.w / 2, cy = o.y + o.h / 2, L = Math.max(o.w, o.h), sw = o.sw || "0.9", rx = o.rx;
      const [fi, fg] = dirGrad(o.fill, cx, cy, L);
      const [bi, bg] = dirGrad(o.border, cx, cy, L);
      g.appendChild(fg); g.appendChild(bg);
      g.appendChild(svgEl("rect", { x: o.x, y: o.y, width: o.w, height: o.h, rx, fill: "url(#" + fi + ")", stroke: "url(#" + bi + ")", "stroke-width": sw }));
      if (o.gloss !== false){
        const [gi, gg] = dirGrad([{ o: 0, c: "#fff", op: 0.12 }, { o: 0.6, c: "#fff", op: 0 }], cx, cy, L);
        g.appendChild(gg);
        g.appendChild(svgEl("rect", { x: o.x, y: o.y, width: o.w, height: o.h, rx, fill: "url(#" + gi + ")" }));
      }
      if (o.body) o.body(g);
      g.appendChild(svgEl("rect", { class: "ad-sh", x: o.x, y: o.y, width: o.w, height: o.h, rx, fill: "url(#sheen-" + o.sid + ")" }));
      g.appendChild(svgEl("rect", { class: "ad-gl", x: o.x, y: o.y, width: o.w, height: o.h, rx, fill: "none", stroke: "url(#edge-" + o.sid + ")", "stroke-width": sw }));
      g.appendChild(brackets(o.x, o.y, o.w, o.h));
      return g;
    }

    // recompute the sun from the LOCAL clock (self-contained) and re-aim every live tile gradient.
    function applySun(){
      let beta;
      if (reduceMotion){ beta = -0.6; }                    // fixed pleasant upper-left angle, no clock
      else { const now = new Date(), h = now.getHours() + now.getMinutes() / 60; beta = (h - 12) * Math.PI / 12; }
      const ax = AD_SCENE.x + Math.sin(beta) * AD_SUN_R, ay = AD_SCENE.y - Math.cos(beta) * AD_SUN_R;
      let dx = AD_SCENE.x - ax, dy = AD_SCENE.y - ay, len = Math.hypot(dx, dy) || 1;
      sunDir = { dx: dx / len, dy: dy / len };
      const elev = Math.cos(beta), inten = 0.65 + (elev >= 0 ? 0.5 : 0.12) * elev;   // noon ~1.15, dusk ~0.65, midnight ~0.53
      document.documentElement.style.setProperty("--sun", inten.toFixed(3));
      for (const grad of document.querySelectorAll("linearGradient.ad-dir")){
        const gx = +grad.getAttribute("data-cx"), gy = +grad.getAttribute("data-cy"), hh = +grad.getAttribute("data-l") / 2;
        grad.setAttribute("x1", (gx - sunDir.dx * hh).toFixed(2)); grad.setAttribute("y1", (gy - sunDir.dy * hh).toFixed(2));
        grad.setAttribute("x2", (gx + sunDir.dx * hh).toFixed(2)); grad.setAttribute("y2", (gy + sunDir.dy * hh).toFixed(2));
      }
    }
```

- [ ] **Step 4: Light the per-scenario defs.** In `buildActivityDiagram`, after `svg.appendChild(defs);` (~L662) but you can append to `defs` before that — add right after the marker is appended to `defs` (~L661), before `svg.appendChild(defs)`:

```js
      // per-scenario cursor-lit gradients (positioned by the Task-3 rAF; userSpaceOnUse = THIS svg's space)
      const sid = sc.scenarioId;
      const sheen = svgEl("radialGradient", { id: "sheen-" + sid, class: "ad-sheen", gradientUnits: "userSpaceOnUse", cx: "-999", cy: "-999", r: "165", fx: "-999", fy: "-999" });
      sheen.appendChild(svgEl("stop", { offset: "0", class: "ss0" }));
      sheen.appendChild(svgEl("stop", { offset: "0.45", class: "ss1" }));
      sheen.appendChild(svgEl("stop", { offset: "1", class: "ss2" }));
      defs.appendChild(sheen);
      const edge = svgEl("linearGradient", { id: "edge-" + sid, class: "ad-edge", gradientUnits: "userSpaceOnUse", x1: "-999", y1: "-999", x2: "-998", y2: "-998" });
      edge.appendChild(svgEl("stop", { offset: "0", class: "es0" }));
      edge.appendChild(svgEl("stop", { offset: "0.5", class: "es0" }));
      edge.appendChild(svgEl("stop", { offset: "0.66", class: "es2" }));
      edge.appendChild(svgEl("stop", { offset: "1", class: "es2" }));
      defs.appendChild(edge);
```

Then in the node dispatch loop (~L723) change `svg.appendChild(actionNode(it));` to pass `sid`:

```js
        if (it.kind !== "fork"){ svg.appendChild(actionNode(it, sid)); continue; }
```

- [ ] **Step 5: Rewrite `actionNode` to use `litTile`** (~L749). Replace the whole function with:

```js
    // a content-sized action/assert node: sun-lit phase-tinted box (rx 3) + centred .ad-nm label,
    // status-styled (failed -> red border; skipped -> muted grey). sid = scenario id for the per-svg sheen/glint.
    function actionNode(it, sid){
      const st = it.steps[0], status = (st.status || "").toLowerCase(), tint = phaseColor(it.phase);
      const fillC = status === "skipped"
        ? "color-mix(in srgb, var(--ad-grey) 12%, var(--ad-panel))"
        : "color-mix(in srgb, " + tint + " 15%, var(--ad-panel))";
      const hue = status === "skipped" ? "var(--ad-grey)" : status === "failed" ? "var(--fail)" : tint;
      const g = litTile({
        x: it.x, y: it.y, w: it.w, h: it.h, rx: 3, sid,
        sw: status === "failed" ? "1.3" : "1",
        fill: [{ o: 0, c: fillC }, { o: 1, c: fillC, op: 0.95 }],
        border: [{ o: 0, c: hue, op: 0.9 }, { o: 1, c: hue, op: 0.5 }],
        label: nodeLabel(st),
        body: (g) => {
          const label = svgEl("text", { class: "ad-nm", x: it.x + it.w / 2, y: it.y + it.h / 2, "text-anchor": "middle", "dominant-baseline": "central" });
          if (status === "skipped") label.style.fill = "var(--ad-grey)";
          label.textContent = nodeLabel(st);
          g.appendChild(label);
        },
      });
      g.setAttribute("data-step", st.stepId);   // click -> focusStep delegation (existing)
      return g;
    }
```

- [ ] **Step 6: Light the graph branch nodes.** In `buildForkGraph` (~L872–895), replace the branch-node `for (const b of boxes){ … }` body (from `const s = b.s, status = …` through `g.appendChild(node);` ~L873–895) with a `litTile` build:

```js
      for (const b of boxes){
        const s = b.s, status = (s.status || "").toLowerCase();
        const fillC = status === "skipped"
          ? "color-mix(in srgb, var(--ad-grey) 12%, var(--ad-panel))"
          : "color-mix(in srgb, " + tint + " 15%, var(--ad-panel))";
        const hue = status === "skipped" ? "var(--ad-grey)" : status === "failed" ? "var(--fail)" : tint;
        const bxL = bx;                                   // capture for the body closure
        const node = litTile({
          x: bxL, y: branchTop, w: b.w, h: NODE_H, rx: 3, sid: sc.scenarioId,
          sw: status === "failed" ? "1.3" : "1",
          fill: [{ o: 0, c: fillC }, { o: 1, c: fillC, op: 0.95 }],
          border: [{ o: 0, c: hue, op: 0.9 }, { o: 1, c: hue, op: 0.5 }],
          label: nodeLabel(s),
          body: (g) => {
            const label = svgEl("text", { class: "ad-nm", x: bxL + b.w / 2, y: branchTop + NODE_H / 2, "text-anchor": "middle", "dominant-baseline": "central" });
            if (status === "skipped") label.style.fill = "var(--ad-grey)";
            label.textContent = nodeLabel(s);
            g.appendChild(label);
          },
        });
        node.setAttribute("data-step", s.stepId);
        g.appendChild(node);

        const rtype = producedResourceType(s, sc);
        ports.push({
          x: bx + b.w / 2, y: branchBot,
          color: rtype ? typeColor(rtype) : "var(--ad-control)", resourceType: rtype, stepId: s.stepId,
        });
        bx += b.w + AD_GRAPH_BRANCH_GAP;
      }
```

- [ ] **Step 7: Wire the bootstrap.** Replace the two bootstrap lines (~L619–620):

```js
    renderAll();
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(renderAll);
```

with (sun set before first paint so tiles are born lit; slow clock only when motion is allowed):

```js
    applySun();                                   // set sunDir + --sun before first paint -> tiles born lit
    renderAll();
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(renderAll);
    if (!reduceMotion) setInterval(applySun, 60000);   // the sun barely moves; re-aim tiles each minute
```

- [ ] **Step 8: Re-emit and verify.** Run the fixture (both themes). Expected: action/assert nodes and the graph branch nodes now carry a **whisper-subtle** directional fill, a lit border (brighter on the light-facing side), and a faint top gloss — matching the resting look of `.git/sdd/mockup-hover-sheen.html` (open it side-by-side). Both themes correct; the diagram is otherwise unchanged (cards/bars still flat — those are Task 2); no console errors. (Hover sheen/glint not active yet — Task 3.)

- [ ] **Step 9: Guard the C# surface** — `dotnet test Raun.slnx -c Debug` (240/240; snapshot unchanged — model untouched) and `dotnet build Raun.slnx -warnaserror` (0 warnings).

- [ ] **Step 10: Commit**

```bash
jj commit -m "report: sun engine + lit action/branch nodes (per-tile directional fill/border/gloss, time-of-day light)"
```

---

## Task 2: Light cards, bars, the timeline cell, and the active pill segment

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (`cardEl` ~L1618; `buildForkGraph` fork/join bars ~L862–915; `buildForkCell` walls ~L1037–1068; `buildForkToggle` active segment ~L824–826; thread `sid` into `cardEl` from `buildObjectFlow`)

**Interfaces:**
- Consumes: `litTile`, `dirGrad`, `sunDir` (Task 1); existing `cardEl(card)`, `contrastInk`, `CARD_HEADER_H`, `AD_GRAPH_BAR_H`, `WALL`.
- Produces: `cardEl(card, sid)` (now takes `sid`); lit fork/join bars; lit timeline panel; lit active pill segment.

- [ ] **Step 1: A small lit-bar helper.** Bars are structural (no sheen/focus) — they get a directional **slate** fill only. Add beside `litTile` (Task 1, ~after `litTile`):

```js
    // a structural bar/wall: slate directional fill only (no sheen/glint/focus). Returns a <rect>; the caller
    // must also append the returned gradient (use litBar(parent, …) to append both).
    function litBar(parent, x, y, w, h, rx){
      const [id, grad] = dirGrad(
        [{ o: 0, c: "color-mix(in srgb, var(--ad-wall) 70%, #fff)" }, { o: 1, c: "var(--ad-wall)" }],
        x + w / 2, y + h / 2, Math.max(w, h));
      parent.appendChild(grad);
      const r = svgEl("rect", { x, y, width: w, height: h, fill: "url(#" + id + ")" });
      if (rx != null) r.setAttribute("rx", rx);
      parent.appendChild(r);
      return r;
    }
```

- [ ] **Step 2: Light the graph fork + join bars.** In `buildForkGraph`: replace the fork-bar block (~L862–865):

```js
      // fork bar (top, full slot width, slate)
      const fork = svgEl("rect", { x: it.x, y: forkBarY, width: it.w, height: AD_GRAPH_BAR_H, rx: 2.5 });
      fork.style.fill = "var(--ad-wall)";
      g.appendChild(fork);
```

with:

```js
      // fork bar (top, full slot width, slate, sun-lit)
      litBar(g, it.x, forkBarY, it.w, AD_GRAPH_BAR_H, 2.5);
```

and in the join-bar loop (~L907–914) replace each `join.appendChild(svgEl("rect", {…}))` with a `litBar(join, …)` call. Replace:

```js
      const join = svgEl("g"); join.style.fill = "var(--ad-wall)";
      let segX = it.x;
      for (const gx of gapXs){
        const gs = gx - GAP / 2;
        if (gs > segX) join.appendChild(svgEl("rect", { x: segX, y: joinY, width: gs - segX, height: AD_GRAPH_BAR_H }));
        segX = gx + GAP / 2;
      }
      if (it.x + it.w > segX) join.appendChild(svgEl("rect", { x: segX, y: joinY, width: it.x + it.w - segX, height: AD_GRAPH_BAR_H }));
      g.appendChild(join);
```

with:

```js
      const join = svgEl("g");
      let segX = it.x;
      for (const gx of gapXs){
        const gs = gx - GAP / 2;
        if (gs > segX) litBar(join, segX, joinY, gs - segX, AD_GRAPH_BAR_H);
        segX = gx + GAP / 2;
      }
      if (it.x + it.w > segX) litBar(join, segX, joinY, it.x + it.w - segX, AD_GRAPH_BAR_H);
      g.appendChild(join);
```

- [ ] **Step 3: Light the timeline cell panel + walls.** In `buildForkCell`: change the cell panel (~L958–960) to a lit panel — replace:

```js
      const panel = svgEl("rect", { x: cellX, y: cellY, width: cellW, height: cellH, rx: 2 });
      panel.style.fill = "var(--ad-cell)";
      g.appendChild(panel);
```

with (panel keeps its flat `--ad-cell` fill but gains a lit border so the "block" reads under the sun):

```js
      const panel = svgEl("rect", { x: cellX, y: cellY, width: cellW, height: cellH, rx: 2 });
      panel.style.fill = "var(--ad-cell)";
      const [pbi, pbg] = dirGrad([{ o: 0, c: "var(--ad-card-border-hi, var(--ad-wall))" }, { o: 1, c: "var(--ad-card-border)" }], cellX + cellW / 2, cellY + cellH / 2, Math.max(cellW, cellH));
      panel.setAttribute("stroke", "url(#" + pbi + ")"); panel.setAttribute("stroke-width", "1");
      g.appendChild(pbg); g.appendChild(panel);
```

Then light the four walls — replace the fork bar (~L1037–1039), join-bar segments (~L1051–1060 inner rects), and left/right walls (~L1063–1068) `var(--ad-wall)` rects with `litBar` calls. Replace the fork bar:

```js
      const fork = svgEl("rect", { x: cellX, y: cellY, width: cellW, height: WALL });
      fork.style.fill = "var(--ad-wall)";
      g.appendChild(fork);
```

with `litBar(g, cellX, cellY, cellW, WALL);`. Replace the join block:

```js
      const join = svgEl("g");
      join.style.fill = "var(--ad-wall)";
      let segX = cellX;
      for (const gx of gapXs){
        const gs = gx - GAP / 2;
        if (gs > segX) join.appendChild(svgEl("rect", { x: segX, y: joinY, width: gs - segX, height: WALL }));
        segX = gx + GAP / 2;
      }
      if (cellRight > segX) join.appendChild(svgEl("rect", { x: segX, y: joinY, width: cellRight - segX, height: WALL }));
      g.appendChild(join);
```

with:

```js
      const join = svgEl("g");
      let segX = cellX;
      for (const gx of gapXs){
        const gs = gx - GAP / 2;
        if (gs > segX) litBar(join, segX, joinY, gs - segX, WALL);
        segX = gx + GAP / 2;
      }
      if (cellRight > segX) litBar(join, segX, joinY, cellRight - segX, WALL);
      g.appendChild(join);
```

Replace the left + right walls:

```js
      const lw = svgEl("rect", { x: cellX, y: cellY, width: WALL, height: cellH });
      lw.style.fill = "var(--ad-wall)";
      g.appendChild(lw);
      const rw = svgEl("rect", { x: cellRight - WALL, y: cellY, width: WALL, height: cellH });
      rw.style.fill = "var(--ad-wall)";
      g.appendChild(rw);
```

with:

```js
      litBar(g, cellX, cellY, WALL, cellH);
      litBar(g, cellRight - WALL, cellY, WALL, cellH);
```

- [ ] **Step 4: Light the entity cards.** Rewrite `cardEl` (~L1618) to wrap the body in `litTile` (lit card body + colored header on top + sheen/glint + brackets + focusable). Replace the whole function with:

```js
    // a card <g>: sun-lit --ad-card body (rx 1) under a colored type-header band; .ad-oth type + .ad-okn key.
    // sid = scenario id for the per-svg sheen/glint (threaded from buildObjectFlow).
    function cardEl(card, sid){
      return litTile({
        x: card.x, y: card.y, w: card.w, h: card.h, rx: 1, sid, sw: "1.1", gloss: false,
        fill: [{ o: 0, c: "var(--ad-card)" }, { o: 1, c: "var(--ad-card)", op: 0.95 }],
        border: [{ o: 0, c: "var(--ad-card-border-hi, var(--ad-wall))" }, { o: 1, c: "var(--ad-card-border)" }],
        label: "object " + (card.type || "") + " " + (card.key == null ? "" : card.key),
        body: (g) => {
          const hdr = svgEl("rect", { x: card.x, y: card.y, width: card.w, height: CARD_HEADER_H, rx: 1 });
          hdr.style.fill = card.color;
          g.appendChild(hdr);
          const tt = svgEl("text", { class: "ad-oth", x: card.x + card.w / 2, y: card.y + CARD_HEADER_H / 2 + 0.3, "text-anchor": "middle", "dominant-baseline": "central" });
          tt.style.fontSize = CARD_TYPE_FS + "px"; tt.style.fill = contrastInk(card.color);
          tt.textContent = (card.type || "").toUpperCase();
          g.appendChild(tt);
          const kt = svgEl("text", { class: "ad-okn", x: card.x + card.w / 2, y: card.y + CARD_HEADER_H + (card.h - CARD_HEADER_H) / 2, "text-anchor": "middle", "dominant-baseline": "central" });
          kt.style.fontSize = CARD_KEY_FS + "px";
          kt.textContent = card.key == null ? "" : String(card.key);
          g.appendChild(kt);
        },
      });
    }
```

`stackCardEl`/`greyCardEl` call `cardEl(card)` internally and add stacks/badges — update their internal `cardEl(card)` call to `cardEl(card, sid)` and thread `sid` into both (`function stackCardEl(card, onClick, sid)` / `function greyCardEl(card, onClick, sid)`); the back-stack rects and badges stay flat (they are depth/quantity cues, not tiles).

- [ ] **Step 5: Thread `sid` into the card calls.** In `buildObjectFlow` (search for `cardEl(`, `stackCardEl(`, `greyCardEl(` — all within `buildObjectFlow`, which has `sc` in scope), pass `sc.scenarioId` as the new last argument to each call (e.g. `cardEl(card)` → `cardEl(card, sc.scenarioId)`; `stackCardEl(card, onClick)` → `stackCardEl(card, onClick, sc.scenarioId)`; same for `greyCardEl`).

- [ ] **Step 6: Light the active pill segment** (G5). In `buildForkToggle` (~L824–826), the active segment box is a flat accent fill. Replace:

```js
        const box = svgEl("rect", { x: sx, y: trackY + 1.5, width: s.w, height: H - 8, rx: RX - 2 });
        box.style.fill = active ? "var(--accent)" : "transparent";
        seg.appendChild(box);
```

with (active segment gets a directional accent gradient so it shades under the same sun):

```js
        const box = svgEl("rect", { x: sx, y: trackY + 1.5, width: s.w, height: H - 8, rx: RX - 2 });
        if (active){
          const [ai, ag] = dirGrad([{ o: 0, c: "color-mix(in srgb, var(--accent) 78%, #fff)" }, { o: 1, c: "var(--accent)" }], sx + s.w / 2, trackY + 1.5 + (H - 8) / 2, Math.max(s.w, H - 8));
          seg.appendChild(ag); box.setAttribute("fill", "url(#" + ai + ")");
        } else { box.style.fill = "transparent"; }
        seg.appendChild(box);
```

- [ ] **Step 7: Re-emit and verify** (both themes). Expected: entity cards now carry a lit body + lit border + faint hover affordance structure (sheen comes alive in Task 3); fork/join bars and the timeline cell walls (toggle a fork to timeline to check) read with a soft slate directional sheen; the active pill segment (hover a fork) shades directionally. Compare resting look against `.git/sdd/mockup-hover-sheen.html`. **Multi-SVG check:** a second scenario's nodes/cards are lit identically (one sun). No console errors; object-flow edges/cards still attach correctly (`.has-em` hover still dims off-path).

- [ ] **Step 8: Guard the C# surface** — `dotnet test Raun.slnx -c Debug` (240/240) + `dotnet build Raun.slnx -warnaserror` (0 warnings).

- [ ] **Step 9: Commit**

```bash
jj commit -m "report: extend sun lighting to cards, fork/join bars, timeline cell, and the active pill segment"
```

---

## Task 3: Cursor fast-path — hover sheen + rim glint (hovered SVG only)

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (sun-engine section ~after `applySun` — add the cursor state + rAF; `report` already in scope at module level ~L406)

**Interfaces:**
- Consumes: `report` (the `<main id="report">` element, module-level), `sunDir`, `AD_SCENE`, `reduceMotion`, the per-svg `radialGradient.ad-sheen` / `linearGradient.ad-edge` (Task 1), `.ad-lit` tiles (Tasks 1–2).
- Produces: a single delegated `pointermove` on `report` + a guarded `requestAnimationFrame` loop that positions only the **active** SVG's sheen centre + the hovered tile's edge span.

- [ ] **Step 1: Add the cursor fast-path.** Insert after `applySun()` (Task 1, in the sun-engine section). The loop eases the sheen centre toward the cursor and lands the rim glint on the hovered tile's far edge (oriented by `sunDir`, per D2 — the cursor does not re-angle tiles):

```js
    // ---- cursor fast-path (spec §3/§7.4): sheen + rim glint follow the pointer, in the HOVERED svg ONLY.
    // Per-tile gradients are sun-only (D2), so this writes at most 2 nodes/frame: the active svg's #sheen-<id>
    // centre (eased toward the cursor) and #edge-<id> span (on the hovered tile's far edge). Guarded: the rAF
    // self-stops when the cursor settles; reduced-motion skips it entirely.
    let actSvg = null, actSheen = null, actEdge = null, tileBB = null;
    let tgX = 0, tgY = 0, cuX = 0, cuY = 0, rafId = 0;
    function lightFrom(svg){
      if (svg === actSvg) return;
      actSvg = svg;
      actSheen = svg ? svg.querySelector("radialGradient.ad-sheen") : null;
      actEdge = svg ? svg.querySelector("linearGradient.ad-edge") : null;
    }
    function tick(){
      rafId = 0;
      if (actSvg && !actSvg.isConnected){ lightFrom(null); tileBB = null; return; }   // dropped by a rerender
      let moving = false;
      if (actSheen){
        if (Math.abs(tgX - cuX) + Math.abs(tgY - cuY) > 0.1){ cuX += (tgX - cuX) * 0.12; cuY += (tgY - cuY) * 0.12; moving = true; }
        else { cuX = tgX; cuY = tgY; }
        actSheen.setAttribute("cx", cuX.toFixed(1)); actSheen.setAttribute("cy", cuY.toFixed(1));
        actSheen.setAttribute("fx", cuX.toFixed(1)); actSheen.setAttribute("fy", cuY.toFixed(1));
      }
      if (actEdge && tileBB){                                  // single glint, bright on the FAR edge (away from the sun)
        const span = 0.5 * Math.hypot(tileBB.width, tileBB.height) + 7;
        const ex = tileBB.x + tileBB.width / 2, ey = tileBB.y + tileBB.height / 2;
        actEdge.setAttribute("x1", (ex - sunDir.dx * span).toFixed(1)); actEdge.setAttribute("y1", (ey - sunDir.dy * span).toFixed(1));
        actEdge.setAttribute("x2", (ex + sunDir.dx * span).toFixed(1)); actEdge.setAttribute("y2", (ey + sunDir.dy * span).toFixed(1));
      }
      if (moving) rafId = requestAnimationFrame(tick);         // keep going only while easing; settles when idle
    }
    function ensureRaf(){ if (!rafId) rafId = requestAnimationFrame(tick); }
    if (!reduceMotion){
      report.addEventListener("pointermove", (e) => {
        const svg = e.target && e.target.closest ? e.target.closest("svg.actdiag") : null;
        lightFrom(svg);
        if (!svg){ tileBB = null; return; }
        const m = svg.getScreenCTM(); if (!m) return;
        const pt = svg.createSVGPoint(); pt.x = e.clientX; pt.y = e.clientY;
        const loc = pt.matrixTransform(m.inverse());
        tgX = loc.x; tgY = loc.y;
        const tile = e.target.closest(".ad-lit");
        tileBB = tile ? tile.getBBox() : null;                // BBox in THIS svg's user space (incl. the brackets margin — fine)
        ensureRaf();
      });
    }
```

- [ ] **Step 2: Re-emit and static-verify** (both themes). At rest the diagram is unchanged from Task 2 (sheen/glint hidden). No console errors.

- [ ] **Step 3: Drive the hover with a Playwright script.** Write a throwaway `drive-sheen.cjs` (gitignored root scratch):

```js
const { chromium } = require("playwright");
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage({ viewport: { width: 1180, height: 1600 } });
  const url = "file://" + process.cwd().replace(/\\/g, "/") +
    "/samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html?theme=dark";
  await p.goto(url);
  const node = p.locator(".ad-lit").first();
  const box = await node.boundingBox();
  await p.mouse.move(box.x + box.width / 2, box.y + box.height / 2);   // hover a tile
  await p.waitForTimeout(400);                                          // let the ease settle
  await p.screenshot({ path: "sheen-hover.png" });
  await b.close();
})();
```

Run: `node drive-sheen.cjs`. Inspect `sheen-hover.png`: the hovered tile shows a soft surface **sheen** (broad, faint, centred near the cursor) + a sharp **rim glint** on the edge away from the sun — compare against hovering a tile in `.git/sdd/mockup-hover-sheen.html`. Off-path neighbours dim (the shipped `.has-em`); the hovered tile keeps full opacity + its sheen.

- [ ] **Step 4: Verify the which-SVG mapping** (the multi-SVG proof). Extend the driver to hover a tile in a **second** scenario card (`.ad-lit` under a later `svg.actdiag`) and screenshot: the sheen/glint appear in **that** SVG, and the first SVG rests at sun-only (no sheen). Confirms `closest('svg.actdiag')` + per-svg `getScreenCTM` route the light to the right diagram.

- [ ] **Step 5: Verify time-of-day + reduced motion.** (a) Time: in a driver, override the clock before `goto` with `await p.addInitScript(() => { const F = Date; const fixed = new F('2026-06-21T12:00:00'); globalThis.Date = class extends F { constructor(...a){ return a.length ? new F(...a) : fixed; } static now(){ return fixed.getTime(); } }; });` then screenshot — at noon the light is near-overhead and brighter (`--sun ≈ 1.15`); repeat with `T19:00:00` (dusk, dimmer, low angle). Confirm the per-tile border directional read shifts. (b) Reduced motion: `const p = await b.newPage({ viewport:{width:1180,height:1600}, reducedMotion: "reduce" });` then `goto` + screenshot — tiles are statically lit (fixed angle), hovering shows **no** sheen/glint (suppressed), no errors.

- [ ] **Step 6: Guard the C# surface** — `dotnet test Raun.slnx -c Debug` (240/240) + `dotnet build Raun.slnx -warnaserror` (0 warnings).

- [ ] **Step 7: Commit**

```bash
jj commit -m "report: cursor-tracked hover sheen + rim glint in the hovered scenario svg (eased rAF, per-svg gradients)"
```

---

## Task 4: Uniform corner-bracket focus + keyboard parity (incl. the pill)

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (CSS pill rule ~L262; `buildForkToggle` segment ~L821; `buildScenarioCard` delegated listeners ~L1796–1805)

**Interfaces:**
- Consumes: `litTile` tiles already `tabindex=0 role=button` with brackets (Tasks 1–2); the delegated `click`/`keydown` listeners + `focusStep`/`onForkSet` in `buildScenarioCard` (Phase 1).
- Produces: pill segments wearing corner brackets (replacing the Phase-1 outline); Enter/Space on any focused tile activates it (parity with click).

- [ ] **Step 1: Migrate the pill segment from outline to brackets.** In `buildForkToggle`, the `.ad-fork-seg` group is built at ~L821. After `seg.setAttribute("aria-label", …)` (~L823), append a tight bracket reticle sized to the segment box. Change the segment-box block to add brackets after the box+text are appended — at the end of the `for (const s of segs){ … }` body, just before `pill.appendChild(seg);` (~L831), insert:

```js
        seg.appendChild(brackets(sx, trackY + 1.5, s.w, H - 8));   // corner-bracket focus (replaces the outline)
```

- [ ] **Step 2: Drop the pill outline CSS; add bracket reveal for the segment.** Replace the Phase-1 rule (~L262):

```css
  .ad-fork-seg:focus-visible{ outline:2px solid var(--ring, var(--accent)); outline-offset:2px; border-radius:3px; }
```

with (segments now use the same `.brk` reticle as tiles; brackets are slightly tighter via the small box passed in Step 1):

```css
  .ad-fork-seg{ outline:none; }
  .ad-fork-seg .brk{ opacity:0; transition:opacity .14s ease; pointer-events:none; }
  .ad-fork-seg:focus-visible .brk{ opacity:1; }
  @media (prefers-reduced-motion: reduce){ .ad-fork-seg .brk{ transition:none; } }
```

- [ ] **Step 3: Add Enter/Space parity for tiles.** In `buildScenarioCard`, the delegated `keydown` listener (~L1802–1805) currently only handles the fork pill. Extend it so a focused **tile** (node/card → `data-step`) activates on Enter/Space, mirroring the click path. Replace:

```js
        diagramSvg.addEventListener("keydown", (ev) => {
          if (ev.key !== "Enter" && ev.key !== " ") return;
          if (onForkSet(ev.target)){ ev.preventDefault(); }
        });
```

with:

```js
        diagramSvg.addEventListener("keydown", (ev) => {
          if (ev.key !== "Enter" && ev.key !== " ") return;
          if (onForkSet(ev.target)){ ev.preventDefault(); return; }
          const target = ev.target && ev.target.closest ? ev.target.closest("[data-step]") : null;
          if (target){ focusStep(target.dataset.step, { toggle: true }); ev.preventDefault(); }
        });
```

- [ ] **Step 4: Re-emit and verify keyboard focus with a Playwright driver.** Write `drive-focus.cjs` (gitignored): load the report, press `Tab` repeatedly, screenshot after a few tabs landing on a node, a card, and a pill segment — each shows the **corner-bracket reticle** (`.brk` visible), distinct from the hover sheen. Confirm Enter on a focused node opens its drill (`focusStep`), and Enter on a focused pill segment toggles the fork (Phase-1 behavior intact). Pattern:

```js
const { chromium } = require("playwright");
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage({ viewport: { width: 1180, height: 1600 } });
  const url = "file://" + process.cwd().replace(/\\/g, "/") +
    "/samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html?theme=dark";
  await p.goto(url);
  for (let i = 0; i < 6; i++){ await p.keyboard.press("Tab"); }
  await p.screenshot({ path: "focus-brackets.png" });          // expect a corner-bracket reticle on the focused tile
  await b.close();
})();
```

Run: `node drive-focus.cjs`. Inspect `focus-brackets.png` against the mock's focus state (Tab in `.git/sdd/mockup-hover-sheen.html`). Re-run with `?theme=light`.

- [ ] **Step 5: Full both-theme + reduced-motion regression.** Re-run the fixture screenshots (Task fixture, both themes) for a final whole-feature look; re-run the Task 3 reduced-motion driver to confirm static lighting + suppressed sheen + brackets still appear on focus (focus is not motion). Confirm a non-fork scenario and a second scenario light independently.

- [ ] **Step 6: Guard the C# surface** — `dotnet test Raun.slnx -c Debug` (240/240) + `dotnet build Raun.slnx -warnaserror` (0 warnings).

- [ ] **Step 7: Commit**

```bash
jj commit -m "report: uniform corner-bracket keyboard focus on all tiles incl. the pill + Enter/Space parity"
```

---

## Plan self-review (run before handing off)

1. **Spec coverage** (`2026-06-21-report-sun-lighting-design.md`): two decoupled paths §3 → Task 1 (sun) + Task 3 (cursor) ✓; per-SVG `#sheen`/`#edge` instances §4.1 → Task 1 Step 4 ✓; class-keyed stop CSS §4.1 → Task 1 Step 2 ✓; cursor→which-SVG via delegated `pointermove` on `report` + per-svg `getScreenCTM` §4.2 → Task 3 Step 1 ✓; rerender pruning (no tile registry; `isConnected` guard; born-lit at build) §4.3 → Task 1 (build-time `dirGrad` + `applySun` over live DOM) + Task 3 (`isConnected`) ✓; perf §4.4 (2 nodes/frame, self-stopping rAF) → Task 3 ✓; D3 uniform brackets incl. pill §4.5 → Task 4 ✓; coexistence with `.has-em` §5 → unchanged + Task 2/3 verify ✓; reduced-motion §5 → Task 1 (`applySun` fixed angle, no interval) + Task 3 (skip rAF) + CSS (suppress sheen/glint) ✓; theme vars §6 → Task 1 Step 1 (all four palette blocks) ✓; C# untouched §7 → no model/builder edits, every task guards the snapshot ✓. Visual language (fork-graph spec §7): sun math/intensity → `applySun` ✓; `userSpaceOnUse` fixed-L centred gradients → `dirGrad` ✓; sheen radial r165 + edge sharp-cutoff → Task 1 defs + Task 3 placement ✓; corner brackets L5.4/o1.7/sw0.85 → `brackets()` ✓; gentle fade in .22 / out .5 → Task 1 CSS ✓.
2. **Placeholder scan:** every code step carries real code; "tune against the mock" notes name concrete starting values + the locked file (house style). No `TODO`/`TBD`/"add error handling".
3. **Type/name consistency:** `sunDir{dx,dy}`, `AD_SUN_R`, `AD_SCENE`, `reduceMotion`, `dirGrad(stops,cx,cy,L)->[id,grad]`, `brackets(x,y,w,h)->g`, `litTile(o)`, `litBar(parent,x,y,w,h,rx?)`, `applySun()`, `actionNode(it,sid)`, `cardEl(card,sid)`, `#sheen-<sid>`/`.ad-sheen`, `#edge-<sid>`/`.ad-edge`, `.ad-dir`+`data-cx/cy/l`, `.ad-lit`/`.ad-sh`/`.ad-gl`/`.brk` are used identically across Tasks 1–4. `litTile`'s `o.fill`/`o.border` stop shape `{o,c,op?}` matches `dirGrad`. The cursor path reads `radialGradient.ad-sheen`/`linearGradient.ad-edge` — the exact classes Task 1 Step 4 sets.

Note: `--ad-card-border-hi` is referenced with a `var(--ad-wall)` fallback (`var(--ad-card-border-hi, var(--ad-wall))`) because the template defines `--ad-card-border` but not `-hi`; the fallback keeps it palette-correct without adding a var. If the lit card border reads too flat against the mock, add `--ad-card-border-hi` to both themes during Task 2 Step 7 tuning (mock dark `#454d5c` / light `#eef2f7`).

---

## Execution Handoff

Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks (REQUIRED SUB-SKILL: superpowers:subagent-driven-development).
2. **Inline Execution** — execute tasks in this session with checkpoints (REQUIRED SUB-SKILL: superpowers:executing-plans).

Either way: the verify loop (headless both-theme render + the Task 3/4 driver scripts + `dotnet test` / `-warnaserror`) is the per-task green gate. `jj`-only, no trailers. Keep a ledger (e.g. `.git/sdd/sun-progress.md`). Land via `superpowers:finishing-a-development-branch` (jj — advance `main` only with consent; local-only, no remote).
</content>
