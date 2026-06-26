// ASS-1753 — screenshot the backend-free run-activity pill harness with the
// globally-available Playwright chromium. Writes labelled --mocked PNGs into results/.
import { pathToFileURL } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

// playwright lives in an npx cache (no node_modules in this worktree); resolve it
// explicitly via a require anchored at PLAYWRIGHT_PKG_BASE (passed by the runner).
const pkgBase = process.env.PLAYWRIGHT_PKG_BASE;
if (!pkgBase) { console.error('set PLAYWRIGHT_PKG_BASE to a dir whose node_modules has playwright'); process.exit(2); }
const requireFrom = createRequire(join(pkgBase, 'noop.js'));
const { chromium } = requireFrom('playwright');

const here = dirname(fileURLToPath(import.meta.url));
const resultsDir = resolve(here, '..');               // results/
const harness = pathToFileURL(join(here, 'ASS-1753-run-activity-harness.html')).href;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 2 });
await page.goto(harness, { waitUntil: 'networkidle' });
await page.waitForSelector('[data-testid="run-activity-gallery"]');
await page.waitForSelector('[data-scenario="active"] [data-testid="task-card-run-activity"]');

// Full gallery — all four states with pill + tooltip detail.
await page.screenshot({ path: join(resultsDir, 'ASS-1753-run-activity-pill-gallery--mocked.png'), fullPage: true });

// Focused close-up of the Directive-3 row: the cyan "Run aktiv" pill for a live run.
const activeRow = page.locator('[data-scenario="active"]');
await activeRow.scrollIntoViewIfNeeded();
await activeRow.screenshot({ path: join(resultsDir, 'ASS-1753-run-activity-active--mocked.png') });

// Log the rendered labels so the evidence is self-describing.
for (const id of ['active', 'no-active-run', 'failed-backoff', 'failed-idle']) {
  const label = await page
    .locator(`[data-testid="pill-${id}"] [data-testid="task-card-run-activity"]`)
    .innerText().catch(() => '(none)');
  const kind = await page
    .locator(`[data-testid="pill-${id}"] [data-testid="task-card-run-activity"]`)
    .getAttribute('data-run-activity-kind').catch(() => '(none)');
  console.log(`scenario ${id.padEnd(14)} -> kind=${kind} label="${label.trim()}"`);
}

await browser.close();
console.log('screenshots written to', resultsDir);
