# Resource matrix browser checks

The browser checks load the default report with real AppointmentTests data.
The CI and release workflows run them before packing. Install the pinned tools with `npm ci` in this directory, then `npx playwright install chromium`.
The checks require Node.js, the Playwright module, and its Chromium browser. These are development
tools only. The shipped report is self-contained and has no JavaScript dependencies.
If Playwright is installed elsewhere, expose it through NODE_PATH. To use an existing Chromium
executable, set PLAYWRIGHT_CHROMIUM_EXECUTABLE to its path.

Generate a sample report with the normal AppointmentTests command plus --report-html, then run:

    node test/Browser/resource-matrix.cjs /absolute/path/to/raun-report.html
    node test/Browser/scenario-flow.cjs /absolute/path/to/raun-report.html

An optional second argument selects the output directory. The default is artifacts/resource-matrix.
The command preserves every sample row and its order, and it checks ordinary lists for no-resource
scenarios. It also verifies resource selection, captured snapshots, detail expansion, grouping,
failure evidence, and mobile access to the resource columns. It writes previews and screenshots
to the output directory.

Failure, crowded-resource, and differing title/event snapshot cases are named fixtures
derived from the sample. They do not change the sample's actual results.

`coverage-view.cjs` checks the Coverage view with the sample's own coverage: the whole-run score, the flow's
width and type size, keyboard drill-down to source, sorting, band filtering, collector notes, and phone layout:

    node test/Browser/coverage-view.cjs /absolute/path/to/raun-report.html

`scenario-tags.cjs` checks the tag filter with the sample's own tags: every tag offered once, card
chips without the tag every scenario shares, either-of selection, the filter kept in and restored
from the URL, such as `?tag=area:users`, a stale tag from another run, and phone layout:

    node test/Browser/scenario-tags.cjs /absolute/path/to/raun-report.html

The scenario-flow checks cover the shared timing axis, observed parallel overlap, joins, nested if/switch
branches, untaken alternatives, named prerequisites, keyboard navigation and narrow layouts.

`report-perf.cjs` profiles report loading at scale. It clones the sample's scenarios to the requested
count, renders them with the current template, and prints time to settled, Chromium's script, layout
and style totals, and CPU time by function. Each figure is the median over the runs. The script writes
CPU profiles to the output directory, artifacts/report-perf by default, for inspection in DevTools:

    node test/Browser/report-perf.cjs /absolute/path/to/raun-report.html --scenarios 300 --runs 3
