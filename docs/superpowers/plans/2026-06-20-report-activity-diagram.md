# Report Activity-Diagram Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-scenario Gantt-timeline + object-flow SVG overlay in the HTML report with a single, flat, top-down **SVG activity diagram** per scenario, in both light and dark themes.

**Architecture:** All work is in one embedded template, `src/Raun.Mtp/HtmlReport/report-template.html` (inline HTML/CSS/JS; model injected as JSON at one token). `renderScenario(sc)` is rewritten to build a pure-SVG activity diagram computed from `model.scenarios[i]`. The old Gantt bars / resource rows / `buildFlowOverlay` / flow-highlight machinery are removed. The drill panel, summary chips, theme system, `PALETTE`/`typeColor`, and the time-axis math (`niceAxis`/`fmtTick`) are kept and reused. Source Serif 4 is embedded as base64 woff2.

**Tech Stack:** Hand-authored SVG + vanilla JS inside one HTML file; C#/xUnit tests (`Raun.Mtp.Test`); .NET 10 build; `npx playwright` (chromium) for headless visual verification; `jj` for VCS.

## Global Constraints

Copied verbatim from the spec (`docs/superpowers/specs/2026-06-20-report-activity-diagram-design.md`) — every task implicitly includes these:

- **Self-contained HTML, HARD rule:** inline `<style>`/`<script>` only — **zero** external URLs/CDNs/web-fonts/`@import`. Source Serif 4 → **base64 woff2 embedded** via inline `@font-face`.
- **JSON token:** exactly one `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`; `HtmlReportSink` string-replaces it. Don't break it.
- **Model field names are FIXED** (camelCase serialized); the C# model + builder do **not** change.
- **Both themes:** auto light/dark + `?theme=light|dark`; the light palette must be defined.
- **Keep tests green:** `HtmlReportModelBuilderTests` (the `Verify(json)` snapshot **must NOT change**) and `HtmlReportSinkTests` (substring asserts). **0-warning build.**
- **VCS: `jj` only** — never `git` mutations. Commit with `jj commit -m "..."`. **No `Co-Authored-By` / tooling trailers** in messages.

## Scope clarifications (read before starting)

1. **No decision/merge diamonds.** The model is a step DAG (`steps[].dependsOn[]`) with no branch/conditional concept. The `v17` mock's "Slot free? / Yes / No" diamond is illustrative only and is **out of scope** — real scenarios are linear/fork DAGs. Render: initial → (fork → parallel lanes → join)* → action/assert nodes → final. Decision/merge is deferred until the model gains branch data.
2. **The locked mocks are the visual source of truth.** Where a step says "match the mock," the committed file `docs/superpowers/handoffs/2026-06-20-report-activity-diagram-mockups-s2/v17-gap-crossings.html` is the literal reference for SVG structure, geometry, and constants; the label/collapse treatments are in `.superpowers/brainstorm/9542-1781954515/content/labels-r3.html` and `collapse-r2.html` (gitignored working copies under `_preview/` carry the same content + reference PNGs). Adapt that static SVG into a model-driven renderer; do not reinvent the geometry.
3. **Testing strategy.** This repo has **no JS test runner**, and the template JS has never been unit-tested. So: **TDD the C# observable surface** (self-contained guard, embedded font, preserved/removed markers) with real failing-first cycles; **verify the SVG visuals via the headless Playwright loop against the locked mocks** (the proven loop that produced the design). Each SVG task ends with a concrete headless-verify acceptance, not a C# assert. Do **not** add a JS test framework (scope creep, not in the spec).

## The verification fixture (used by every SVG task)

`samples/AppointmentTests` is the real "Booking an appointment" suite — its **"customer books with parallel arrange"** scenario is an exact match for the mock (Database-clean → Patient & Slot created on parallel lanes → CreateAppointment reads both + creates Appointment → Then reads Appointment).

Generate + render a real report:

```bash
# 1. emit a real report from the sample suite
dotnet run --project samples/AppointmentTests -c Debug -- --report-html
#    → samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html

# 2. headless-render it (chromium is installed; the Playwright MCP defaults to Chrome which is NOT)
R="samples/AppointmentTests/bin/Debug/net10.0/TestResults/raun-report.html"
npx playwright screenshot --browser=chromium --full-page --viewport-size=1100,2200 "$R" out-dark.png
npx playwright screenshot --browser=chromium --full-page --viewport-size=1100,2200 "file://$(pwd)/$R?theme=light" out-light.png
```

Inspect `out-dark.png` / `out-light.png`; compare the "customer books with parallel arrange" scenario against the locked mock. (Zoom-inspection: crop with PIL — see the `_preview/` PNGs for the pattern.) Re-run after each change.

---

## File structure

| File | Responsibility | Action |
|---|---|---|
| `src/Raun.Mtp/HtmlReport/report-template.html` | The entire report shell + per-scenario activity-diagram renderer (inline CSS/JS) | **Modify** (the only production file touched) |
| `src/Raun.Mtp/HtmlReport/<font>.woff2` (build-time only) | Source Serif 4 source(s) to base64-encode and inline; **not** shipped — the bytes live inline in the template | **Add (transient)** |
| `test/Raun.Mtp.Test/HtmlReportSinkTests.cs` | C# substring guards on the emitted HTML | **Modify** (add guards; keep existing) |

The renderer is organized as labelled sections inside the template's single `<script>` (layout → control → fork → object-flow → labels → collapse → interaction) and matching CSS blocks, so each task touches a focused region.

---

## Task 1: Embed Source Serif 4 + self-contained guard

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (`<head>` `<style>`: add `@font-face` + `--ad-font`)
- Test: `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`

**Interfaces:**
- Produces: an inline `@font-face { font-family:'Source Serif 4'; ... src: url(data:font/woff2;base64,...) }` (italic 500 + roman 400/500/600 coverage); CSS var `--ad-font: 'Source Serif 4', Georgia, serif;` on `:root`.

- [ ] **Step 1: Write the failing tests** — append to `HtmlReportSinkTests.cs`, inside the class, reusing the existing `Def()`/`Passed()`/`_dir` helpers:

```csharp
[Fact]
public async Task Report_embeds_the_serif_font_and_links_no_external_assets()
{
    var path = Path.Combine(_dir, "raun-report.html");
    var sink = new HtmlReport.HtmlReportSink(path, new TestTimeProviderUtc(T0));
    var def = Def();
    await sink.PublishAsync(new RunStarted(1));
    await sink.PublishAsync(new ScenarioStarted(def));
    await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
    await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
    await sink.PublishAsync(new RunFinished());

    var html = await File.ReadAllTextAsync(path);
    // Source Serif 4 embedded as base64 woff2 (no CDN/web-font)
    Assert.Contains("@font-face", html, StringComparison.Ordinal);
    Assert.Contains("Source Serif 4", html, StringComparison.Ordinal);
    Assert.Contains("data:font/woff2;base64,", html, StringComparison.Ordinal);
    // self-contained: no external asset references
    Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.Ordinal);
    Assert.DoesNotContain("@import", html, StringComparison.Ordinal);
    Assert.DoesNotContain("<link rel=\"stylesheet\" href=\"http", html, StringComparison.Ordinal);
    Assert.DoesNotContain("<script src=\"http", html, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run it — expect FAIL** (no `@font-face` yet)

Run: `dotnet test test/Raun.Mtp.Test --filter "Report_embeds_the_serif_font_and_links_no_external_assets"`
Expected: FAIL — assertion on `@font-face`.

- [ ] **Step 3: Obtain + encode the font.** Download Source Serif 4 woff2 (OFL, e.g. the google-fonts/fontsource distribution): the **italic** instance covering weight 500 and the **roman** instance covering 400/500/600 (variable woff2 if one file covers each axis; subset to Latin to limit size). Base64-encode each:

```bash
# example; adjust filenames. Produces base64 with no line wraps.
base64 -w0 SourceSerif4-Italic-latin.woff2 > italic.b64
base64 -w0 SourceSerif4-Roman-latin.woff2  > roman.b64
```

- [ ] **Step 4: Inline the `@font-face` + font var** in the `<head>` `<style>` (top of the CSS, before `:root` vars):

```css
@font-face{
  font-family:'Source Serif 4'; font-style:normal; font-weight:400 600; font-display:swap;
  src:url(data:font/woff2;base64,PASTE_ROMAN_B64) format('woff2');
}
@font-face{
  font-family:'Source Serif 4'; font-style:italic; font-weight:400 600; font-display:swap;
  src:url(data:font/woff2;base64,PASTE_ITALIC_B64) format('woff2');
}
:root{ --ad-font:'Source Serif 4', Georgia, serif; }
```

- [ ] **Step 5: Run the new test + the whole sink suite — expect PASS**

Run: `dotnet test test/Raun.Mtp.Test --filter "HtmlReportSinkTests"`
Expected: PASS (new test + the 3 existing).

- [ ] **Step 6: Commit**

```bash
jj commit -m "report: embed Source Serif 4 (base64 woff2) + self-contained guard test"
```

---

## Task 2: Activity-diagram palette + `shade()` helper (both themes)

**Files:**
- Modify: `report-template.html` — the `:root` CSS var blocks (light default, dark `@media`, `[data-theme]` overrides) and the `<script>` helpers region.

**Interfaces:**
- Produces (CSS vars, defined in **all** theme blocks): `--ad-panel, --ad-cell, --ad-cell-row, --ad-wall, --ad-control, --ad-grid, --ad-card, --ad-card-border, --ad-card-ink, --ad-grey, --ad-grey-badge` and a band-tint opacity `--ad-band-op`. Values from spec §5.1 (dark / light).
- Produces (JS): `shade(color, amt)` → string. `amt > 0` lightens toward white, `amt < 0` darkens toward black; parses hex **and** `hsl(...)` (since `typeColor` may return either). And `labelColor(objColor)` = `shade(objColor, theme==='dark' ? +0.22 : -0.22)` where `theme` is read from `document.documentElement.dataset.theme || matchMedia('(prefers-color-scheme: dark)').matches ? 'dark':'light'`.

- [ ] **Step 1: Add the CSS vars** to each existing theme block (light `:root`, dark `@media (prefers-color-scheme: dark) :root`, and both `:root[data-theme=...]` overrides). Dark values:

```css
--ad-panel:#0d1117; --ad-cell:#161b22; --ad-cell-row:#1a212b; --ad-wall:#5f6873;
--ad-control:#5c6571; --ad-grid:#1d2531; --ad-card:#131922; --ad-card-border:#2a313c;
--ad-card-ink:#e6edf3; --ad-grey:#5c6571; --ad-grey-badge:#7e8794; --ad-band-op:.065;
```

Light values:

```css
--ad-panel:#ffffff; --ad-cell:#eef1f5; --ad-cell-row:#e4e8ee; --ad-wall:#aab2bd;
--ad-control:#8b929c; --ad-grid:#d8dde4; --ad-card:#ffffff; --ad-card-border:#d0d7de;
--ad-card-ink:#1f2328; --ad-grey:#aab2bd; --ad-grey-badge:#8b93a0; --ad-band-op:.09;
```

- [ ] **Step 2: Add the `shade()` + `labelColor()` helpers** to the `<script>` (near `typeColor`):

```javascript
function shade(color, amt){ // amt in [-1,1]; >0 -> toward white, <0 -> toward black
  let r,g,b;
  const h = color.match(/^#([0-9a-f]{6})$/i);
  if (h){ const n=parseInt(h[1],16); r=n>>16; g=(n>>8)&255; b=n&255; }
  else { const m=color.match(/hsl\(\s*([\d.]+)[, ]+([\d.]+)%[, ]+([\d.]+)%/i);
    if(!m) return color; const [H,S,L]=[+m[1],+m[2]/100,+m[3]/100];
    const c=(1-Math.abs(2*L-1))*S, x=c*(1-Math.abs((H/60)%2-1)), mm=L-c/2;
    const t=H<60?[c,x,0]:H<120?[x,c,0]:H<180?[0,c,x]:H<240?[0,x,c]:H<300?[x,0,c]:[c,0,x];
    [r,g,b]=t.map(v=>Math.round((v+mm)*255)); }
  const tgt = amt>0?255:0, k=Math.abs(amt);
  const mix=v=>Math.round(v+(tgt-v)*k);
  return "#"+[mix(r),mix(g),mix(b)].map(v=>v.toString(16).padStart(2,"0")).join("");
}
function currentTheme(){
  return document.documentElement.dataset.theme
    || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
}
function labelColor(objColor){ return shade(objColor, currentTheme()==="dark" ? 0.22 : -0.22); }
```

- [ ] **Step 3: Verify the helpers in a browser console** (no C# test — pure JS). Open any generated report, run in DevTools: `shade("#e08544",0.22)` → a lighter orange; `shade("#e08544",-0.22)` → darker; `shade("hsl(30 60% 45%)",0.22)` → a hex string. Confirm no exceptions.

- [ ] **Step 4: Commit**

```bash
jj commit -m "report: activity-diagram palette vars (light+dark) + shade/labelColor helpers"
```

---

## Task 3: Remove old per-scenario viz; add SVG scaffold (frame + spine + initial/final)

**Files:**
- Modify: `report-template.html` — remove the old viz CSS/JS; rewrite `renderScenario(sc)`; add diagram CSS.
- Test: `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`

**Interfaces:**
- Produces (JS): `renderScenario(sc)` → an element containing (a) a header (unchanged), (b) `<svg class="actdiag" ...>` built by `buildActivityDiagram(sc)`, (c) the unchanged drill from `buildScenarioDrill(sc)`. `buildActivityDiagram(sc)` returns an `<svg>` element with a computed `viewBox`.
- Removed: `buildFlowOverlay`, `.flow-svg/.conn/.dock/.flow-label`, `.res-row/.lifeline/.marker`, Gantt bars/lanes, `clearLit/applyLit/litByStep/litByRes/refresh`, `flows/flowCtx/pinned`, `.flow-legend` + its builder. Keep: `niceAxis/fmtTick/typeColor/PALETTE/fmtDur/fmtGen/buildScenarioDrill/focusStep`, chips, theme, title.

- [ ] **Step 1: Write the failing guard tests** — append to `HtmlReportSinkTests.cs` (extend the existing `Writes_a_self_contained_html_file_on_run_finished` body, or add a new fact reusing the same publish sequence):

```csharp
[Fact]
public async Task Renders_the_activity_diagram_and_drops_the_old_overlay()
{
    var path = Path.Combine(_dir, "raun-report.html");
    var sink = new HtmlReport.HtmlReportSink(path, new TestTimeProviderUtc(T0));
    var def = Def();
    await sink.PublishAsync(new RunStarted(1));
    await sink.PublishAsync(new ScenarioStarted(def));
    await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
    await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
    await sink.PublishAsync(new RunFinished());

    var html = await File.ReadAllTextAsync(path);
    Assert.Contains("class=\"actdiag\"", html, StringComparison.Ordinal);   // new SVG diagram
    Assert.Contains("buildActivityDiagram", html, StringComparison.Ordinal);
    Assert.DoesNotContain("buildFlowOverlay", html, StringComparison.Ordinal); // old overlay gone
    Assert.DoesNotContain("flow-svg", html, StringComparison.Ordinal);
    // preserved shell (already asserted elsewhere, re-checked here for this render path)
    Assert.Contains("class=\"drill", html, StringComparison.Ordinal);
    Assert.Contains("data-theme", html, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run it — expect FAIL** (`class="actdiag"` not present).

Run: `dotnet test test/Raun.Mtp.Test --filter "Renders_the_activity_diagram_and_drops_the_old_overlay"`
Expected: FAIL.

- [ ] **Step 3: Remove the old per-scenario viz.** Delete: the `.flow-svg/.conn/.dock/.flow-label/.res-row/.lifeline/.marker/.flow-bar` CSS; the Gantt bar/lane CSS that is only used per-scenario; `buildFlowOverlay()`; the highlight machinery (`clearLit/applyLit/litByStep/litByRes/refresh`); the `flows/flowCtx/pinned/SVGNS`-overlay state; the flow-legend builder + `.flow-legend` CSS. **Keep** `.timeline/.tl-*/.bar/.tick` CSS for now (Task 5 reuses the *math*; remove the unused HTML-timeline CSS only once Task 5 confirms the SVG timeline replaces it). Keep `niceAxis/fmtTick/typeColor/fmtDur/fmtGen/buildScenarioDrill/focusStep` and the chips/theme/`?theme` code.

- [ ] **Step 4: Add diagram CSS classes** (text styles from the mock; sizes in diagram units). In a new `<style>` region:

```css
.actdiag{display:block;width:100%;height:auto;font-family:var(--ad-font)}
.ad-nm{font-weight:400;font-size:7.5px;fill:var(--ad-card-ink)}        /* node label */
.ad-oth{font-weight:600;font-size:5.5px;letter-spacing:.05em;fill:var(--ad-panel)} /* card type header text (on colored band) */
.ad-okn{font-weight:400;font-size:7.5px;fill:var(--ad-card-ink)}       /* card identifier */
.ad-vb{font-style:italic;font-weight:500;font-size:5.5px;paint-order:stroke;stroke:var(--ad-panel);stroke-width:2.4;stroke-linejoin:round} /* verb label + halo */
.ad-phl{font-weight:500;font-size:7px;letter-spacing:.16em}            /* rotated phase label */
.ad-tk{font:500 6.5px ui-monospace,Consolas;fill:var(--ad-control)}    /* ruler ticks */
```

- [ ] **Step 5: Rewrite `renderScenario(sc)` + add `buildActivityDiagram(sc)` scaffold.** Compute the frame and draw, using the mock `v17-gap-crossings.html` as the structural reference (lines ~45–67 frame; ~69–84 spine/initial/final). Scaffold scope for THIS task (later tasks fill the interior):
  - Decide a fixed diagram width (mock uses `viewBox="0 0 680 500"`); compute height from content. Use SVG namespace element creation (`document.createElementNS`).
  - **Bands:** one rect per phase present (Given/When/Then) across full width, `fill:var(--ph-*)` at `opacity:var(--ad-band-op)`; band heights content-driven (Given taller). Divider lines between bands.
  - **Phase tabs + rotated labels** (`.ad-phl`, `transform="rotate(-90 ...)"`), colored per phase.
  - **Spine:** a centred vertical hairline (`stroke:var(--ad-control)`), an **initial** filled circle at top and a **final** ring+core circle at bottom. (Action/fork/object interiors come in Tasks 4–6.)
  - Return the `<svg class="actdiag">`.
  - `renderScenario` appends: header, the svg, then `buildScenarioDrill(sc)` (unchanged).

- [ ] **Step 6: Run C# guard + full suite — expect PASS**

Run: `dotnet test test/Raun.Mtp.Test --filter "HtmlReport"`
Expected: PASS (new guard + existing sink + model-snapshot unchanged).

- [ ] **Step 7: Headless-verify the frame.** Run the fixture loop (top of plan). The "customer books…" scenario should show the 3 bands, rotated phase labels, tabs, the centred spine, initial dot + final ring — empty interior. Matches the mock's frame.

- [ ] **Step 8: Commit**

```bash
jj commit -m "report: drop Gantt+flow overlay; add SVG activity-diagram scaffold (frame, spine, initial/final)"
```

---

## Task 4: Control nodes (action/assert) + content-sizing

**Files:** Modify `report-template.html` (`buildActivityDiagram` control section).

**Interfaces:**
- Consumes: `buildActivityDiagram` scaffold (Task 3); `currentTheme`.
- Produces (JS): `measureText(str, fontPx, weight, italic)` → width in diagram units (append a hidden `<text>` to the live svg, read `getComputedTextLength()`, remove); `nodeBox(label)` → `{w,h}` = measured text + padding, clamped to a min-width floor (mock action box ≈ `112×36`, min-width floor ≈ 64). Control edges drawn between consecutive DAG nodes on the spine with arrowheads (marker defs as in mock lines ~47–52).

- [ ] **Step 1:** Add `measureText` + `nodeBox`. Gate any pre-render measurement on `document.fonts.ready` (Source Serif 4 metrics differ from fallback) — e.g. wrap the top-level scenario render in `document.fonts.ready.then(renderAll)` (and render once immediately with fallback so the page isn't blank, then re-render on ready). Code:

```javascript
function measureText(str, px, weight, italic){
  const t=document.createElementNS("http://www.w3.org/2000/svg","text");
  t.setAttribute("font-family","var(--ad-font)"); t.setAttribute("font-size",px);
  t.setAttribute("font-weight",weight||400); if(italic) t.setAttribute("font-style","italic");
  t.textContent=str; t.setAttribute("visibility","hidden");
  measureSvg().appendChild(t); const w=t.getComputedTextLength(); t.remove(); return w;
}
// measureSvg() returns a persistent off-screen <svg> appended to document.body once.
```

- [ ] **Step 2:** Draw **action/assert nodes** on the spine: for each non-fork step, a content-sized box (`nodeBox(displayName)`), `rx 3`, phase-tinted fill (derive from `--ph-*`), label `.ad-nm` centred; status styling from `step.status` (failed → red stroke; skipped → muted). Connect consecutive nodes with control edges (vertical spine segments + arrowhead marker), reference mock lines ~73–81.

- [ ] **Step 3: Headless-verify** vs the mock: the When `CreateAppointment` box and Then `the appointment should exist` box appear on the spine, content-sized, phase-tinted, joined by the spine with arrowheads. (No fork interior yet — that's Task 5.)

- [ ] **Step 4: Run C# suite — expect PASS** (`dotnet test test/Raun.Mtp.Test --filter "HtmlReport"`).

- [ ] **Step 5: Commit** `jj commit -m "report: content-sized action/assert nodes + control-flow edges"`

---

## Task 5: Fork cell + inline SVG timeline (cropped, centred)

**Files:** Modify `report-template.html` (`buildActivityDiagram` fork section). After this task, remove the now-unused HTML `.timeline/.tl-row/.bar/.tick` CSS that Task 3 kept.

**Interfaces:**
- Consumes: `niceAxis(maxMs)`→`{step,axisMax}`, `fmtTick(ms,axisMax)`, `typeColor`.
- Produces (JS): `buildForkCell(forkSteps, x, y, width)` → an SVG `<g>` drawing the 4-walled cell + inline timeline; returns geometry incl. each lane's **disc-port** position (for Task 6 to attach object edges). Fork detection: steps sharing a `dependsOn`/`groupId` that **overlap** on different `lane`s.

- [ ] **Step 1:** Implement fork detection + `buildForkCell`. Reuse the axis math: `axisMax` from `niceAxis(max(offsetMs+durationMs))` over the fork's lanes; `px = trackWidth/axisMax`; each lane bar at `x = offsetMs*px`, `w = max(minBar, durationMs*px)`. Draw, matching mock lines ~85–116:
  - cell panel `fill:var(--ad-cell)`; **fork bar** (top) + **join bar** (bottom) slate `var(--ad-wall)` with ruler ticks; left/right walls.
  - inner zebra rows `var(--ad-cell-row)`; gridlines `var(--ad-grid)`; `ms` gutter + tick labels (`.ad-tk`, `fmtTick`).
  - one lane bar per parallel step, phase-hued, carrying a white `G/W/T` chip + `.ad-okn` serif label.
  - a **disc port** (filled circle + faint halo) at each lane's production end, colored `typeColor(resourceType)`.
  - **crop** the cell to `max(offsetMs+durationMs)` and **centre** it under the spine.
  - **join-wall gaps:** leave clean gaps where object lines will cross (Task 6 routes through them).

- [ ] **Step 2: Headless-verify** vs mock: the Given fork renders as the packaged cell with the inline timeline (3 lanes: Database clean / Patient Jane / Slot), ruler + `ms` gutter, disc ports, centred under the spine. Compare to `v17` cell.

- [ ] **Step 3:** Remove the leftover HTML-timeline CSS (`.timeline/.tl-row/.tl-gutter/.tl-track/.bar/.tick` and ruler CSS) now that the SVG cell replaces it. Re-run headless to confirm nothing else used them.

- [ ] **Step 4: Run C# suite — expect PASS.**

- [ ] **Step 5: Commit** `jj commit -m "report: fork cell with cropped/centred inline SVG timeline + disc ports"`

---

## Task 6: Object cards + flow edges + ports + arrowheads (clamp/stack, wall-gap crossings)

**Files:** Modify `report-template.html` (`buildActivityDiagram` object-flow section).

**Interfaces:**
- Consumes: disc-port positions (Task 5); `typeColor`; node port insets.
- Produces (JS): `buildObjectFlow(sc, geom)` drawing entity cards + edges + ports. `cardBox(type,key)` → measured card `{w,h}` (header band + body). Port layout: `portSlots(node, side)` clamps data ports to a fixed inset and stacks inward (same inset in/out).

- [ ] **Step 1:** Draw **entity cards** for each resource (Tier 1 / expanded; collapse is Task 8): colored type-header band (`typeColor`) over `var(--ad-card)` body, border `var(--ad-card-border)`, `rx 1`; `.ad-oth` type + `.ad-okn` identifier (mock lines ~155–172). Card width content-sized via `measureText`.

- [ ] **Step 2:** Draw **object-flow edges** producer→card→consumer: uniform width (1.3), object color, **S-curves** with ≤30° off-axis leave/arrive; **input ring** at consumers, **output disc+halo** at producers; **arrowheads** base-centred on the line, centreline aimed at the ring centre, tip just outside (marker defs per object color, mock lines ~48–52, edges ~118–141). Route wall crossings **straight through the clean gap** in the join/side wall (the gap is the port — no glyph). Side-exit + down-loop for divergent objects (e.g. Database down the left margin), mock lines ~126–127.

- [ ] **Step 3:** Implement **clamp + stack** port layout on nodes: data ports at a fixed inset from the node edge, stacking inward; same inset in/out so an output aligns above the matching input; control owns centre (mock CreateAppointment ports ~177–180).

- [ ] **Step 4: Headless-verify** vs mock: Patient & Slot cards under the cell; edges through the join-wall gaps into the cards and on into CreateAppointment's input rings; Appointment card from CreateAppointment's output disc; Database side-exit loop to the Then assertion. Rings/discs/arrowheads as in `v17`.

- [ ] **Step 5: Run C# suite — expect PASS.**

- [ ] **Step 6: Commit** `jj commit -m "report: object entity cards + flow edges, ring/disc ports, gap crossings, clamp/stack"`

---

## Task 7: Edge labels (placement ladder + halo + dashed leader)

**Files:** Modify `report-template.html` (object-flow label section). Visual reference: `collapse-r2`/`labels-r3` content fragments (committed-equivalent under `_preview/`).

**Interfaces:**
- Consumes: edge geometry (Task 6); `labelColor` (Task 2); `measureText`.
- Produces (JS): `placeLabel(verb, edge, obstacles)` → `{x,y,anchor,leader?}` implementing the ladder; draws `.ad-vb` (halo = `var(--ad-panel)`, or the cell bg when inside the cell) + optional dashed leader.

- [ ] **Step 1:** For each edge, the verb = the event's `verb` (per-hop: `create` on producer→card, `read`/`edit`/`delete` on card→consumer), color `labelColor(objColor)`, `.ad-vb` (italic 500 / 5.5 / halo 2.4).

- [ ] **Step 2:** Implement the **placement ladder** (spec §3.2): ① centered knockout-gap at the visible mid-segment → ② keep centered if segment < word (halo overflows) → ③ on collision with a card/cell/neighbour-label box, slide along the wire then step along the normal to the open side → ④ if both sides blocked, place in nearest clear space + **dashed leader** (`stroke-dasharray:2.2 1.8`, object color) back to the wire. Leader only when displaced past a threshold AND a label/port sits between verb and wire. Use measured label boxes vs. card/cell/edge rects for collision (greedy single pass; escalate only if needed).

- [ ] **Step 3:** Set the halo stroke to the **local** background: `var(--ad-panel)` by default, `var(--ad-cell)` when the label sits within the fork cell.

- [ ] **Step 4: Headless-verify** in **both** themes (`?theme=light` and dark): verbs sit in the wire gap / step off as needed, crisp over bands/cell/canvas; no upside-down text (all horizontal). Compare to `labels-r3`.

- [ ] **Step 5: Run C# suite — expect PASS.**

- [ ] **Step 6: Commit** `jj commit -m "report: edge verb labels — placement ladder, bg-knockout halo, dashed leader"`

---

## Task 8: Collapse tiers (group → stack+badge → grey bundle)

**Files:** Modify `report-template.html` (object-flow grouping section).

**Interfaces:**
- Consumes: object-flow render (Tasks 6–7).
- Produces (JS): `tierBundle(objects)` → `{tier, groups}` deciding the tier; renderers for the stack card (Tier 2) and grey bundle card (Tier 3); `expandBundle(node)` click handler (one tier down, re-layout).

- [ ] **Step 1:** Implement `tierBundle` per the thresholds (spec §4.2). Group a producer→consumer bundle's objects by `type`; within a type-group detect verb uniformity.

```javascript
// defaults — tunable constants at top of script
const TIER_INDIVIDUAL_MAX = 4;     // <= this many objects total -> Tier 1
const TIER_TYPEGROUPS_MAX  = 4;     // > this many type-groups   -> Tier 3
const TIER_TOTAL_MAX       = 12;    // > this many total objects -> Tier 3
function tierBundle(objs){
  const byType = groupBy(objs, o => o.type);
  const mixed  = [...byType.values()].some(g => new Set(g.map(o=>o.verb)).size > 1);
  if (objs.length <= TIER_INDIVIDUAL_MAX) return {tier:1, byType};
  if (mixed || byType.size > TIER_TYPEGROUPS_MAX || objs.length > TIER_TOTAL_MAX)
    return {tier:3, byType};
  return {tier:2, byType};
}
```

- [ ] **Step 2:** **Tier 2** render (per type-group): a **stack** (two offset cards behind the main card), a **corner count badge** (object-color circle + count), a **sample identifier** in the body; the colored edge carries the single uniform verb. Reference `collapse-r2` Tier 2.

- [ ] **Step 3:** **Tier 3** render: a card in the normal shape with a **grey blank header band** (`var(--ad-grey)`, no text), blank body, a **grey count badge** (`var(--ad-grey-badge)`, total), a grey stack behind; **grey edges**; one **verb × count** label per occurring verb (producer side e.g. `create ×23`; consumer side `read ×18`, `delete ×5`, stacked), using `.ad-vb` with grey fill. **No `⊕`** — the card is the click target. Reference `collapse-r2` Tier 3.

- [ ] **Step 4:** `expandBundle` — clicking a Tier 2/3 node expands it one tier and re-lays-out that bundle (Tier 3→2→1). The drill panel always lists the full set (unchanged).

- [ ] **Step 5: Headless-verify.** The sample scenarios are small (Tier 1). To exercise Tiers 2–3, temporarily inject a synthetic many-object bundle (duplicate a resource ×12 / mix verbs in a copied report's JSON) and render; confirm the stack+badge and grey bundle match `collapse-r2`. Revert the synthetic data.

- [ ] **Step 6: Run C# suite — expect PASS.**

- [ ] **Step 7: Commit** `jj commit -m "report: collapse tiers — type stack+badge, grey verb-count bundle, click-to-expand"`

---

## Task 9: Interaction, both-theme finalize, end-to-end verification

**Files:** Modify `report-template.html` (interaction); final pass.

**Interfaces:**
- Consumes: the full renderer.
- Produces (JS): hover-emphasis (lighten/raise opacity of an object's full path on card/edge hover; default flat); click an action/assert node → `focusStep(stepId)` (existing drill behavior); click a collapsed node → `expandBundle`.

- [ ] **Step 1:** Wire hover emphasis (CSS `:hover` on edge/card groups, or JS listeners) and node-click → `focusStep`. Remove any dead references to the old `pinned`/`applyLit` left over.

- [ ] **Step 2: Full end-to-end headless verify, both themes.** Regenerate the real report (`dotnet run --project samples/AppointmentTests -c Debug -- --report-html`) and render dark + light (fixture loop). Walk all 4 scenarios; the "customer books with parallel arrange" one must match the `v17` mock (minus the out-of-scope decision diamond). Check: frame, fork cell+timeline, cards, edges, ports, arrowheads, labels (legible both themes), drill still opens on node click, chips/summary intact.

- [ ] **Step 3: Full test + build gate.**

Run: `dotnet test test/Raun.Mtp.Test` (all green, incl. model snapshot unchanged) and `dotnet build Raun.slnx -warnaserror` (0 warnings).
Expected: all PASS, 0 warnings.

- [ ] **Step 4:** Self-contained final check: re-run the Task 1 guard; confirm the emitted report has no `http(s)://` asset refs / `@import` and the font is inline base64.

- [ ] **Step 5: Commit** `jj commit -m "report: activity-diagram interaction (hover emphasis, node->drill) + final both-theme pass"`

- [ ] **Step 6 (optional): Update the handoff/memory.** Note the diagram shipped; the decision/merge gap remains deferred (model has no branch data); the rename thread (Raun → Junction/Tracery/Cascade) is still separate.

---

## Self-review (against the spec)

- **Spec coverage:** §1 scope → Tasks 3,9; §2 visual (frame/spine/fork/crossings/cards/ports/arrowheads) → Tasks 3–6; §3 labels → Task 7; §4 collapse → Task 8; §5 palettes → Task 2 (+ used throughout); §6 pure-SVG/measure/crop/clamp → Tasks 4–6; §7 font → Task 1; §8 data mapping → Tasks 3–8; §9 tests → Tasks 1,3,9. **Gap intentionally scoped out:** §2.2 decision/merge diamonds — no model data; documented in "Scope clarifications" and Task 9 step 6.
- **Placeholder scan:** helper code (`shade`, `tierBundle`, `measureText`, C# tests) is complete; SVG-emission steps reference the committed mock as the literal geometry source (legitimate reference, not a TODO) plus explicit parameterization rules and constants.
- **Type consistency:** `buildActivityDiagram`/`buildForkCell`/`buildObjectFlow`/`placeLabel`/`tierBundle`/`expandBundle`/`shade`/`labelColor`/`measureText`/`nodeBox`/`cardBox` are used consistently across tasks; CSS var names match spec §5.1.
