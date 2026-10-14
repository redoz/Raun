# Resource matrix browser checks

The browser harness exercises the default report with real AppointmentTests data.
CI and release workflows run it before packing. Install its pinned tools with `npm ci` in this directory, then `npx playwright install chromium`.
It requires Node.js, the Playwright module, and its Chromium browser. These are development
tools only; the shipped report remains self-contained and has no JavaScript dependencies.
If Playwright is installed elsewhere, expose it through NODE_PATH. Optionally set
PLAYWRIGHT_CHROMIUM_EXECUTABLE to an existing Chromium executable.

Generate a sample report with the normal AppointmentTests command plus --report-html, then run:

    node test/Browser/resource-matrix.cjs /absolute/path/to/raun-report.html

An optional second argument selects the output directory (default: artifacts/resource-matrix).
The command preserves every sample row and its order, checks ordinary lists for no-resource
scenarios, and verifies resource selection, captured snapshots, detail expansion, grouping,
failure evidence, and mobile access to the resource columns. Previews and screenshots are written
to the output directory.

Failure, crowded-resource, and differing title/event snapshot cases are clearly named fixtures
derived from the sample. They do not change the sample's actual results.
