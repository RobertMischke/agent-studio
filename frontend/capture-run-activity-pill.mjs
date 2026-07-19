// ASS-1751 — capture run-activity pill evidence (backend-free mockup at :4025).
// Writes screenshots into the JOB folder results/ so the auto-review gate sees them.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const BASE = process.env.PW_BASE_URL || 'http://127.0.0.1:4025/';
const OUT = process.env.OUT_DIR
  || 'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/tasks/001/ASS-1751/results';

mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 2 });
await page.goto(BASE, { waitUntil: 'networkidle' });
await page.waitForSelector('[data-testid="run-activity-gallery"]');

// Full gallery — all four states side by side with pill + tooltip detail.
await page.screenshot({ path: `${OUT}/run-activity-pill-gallery.png`, fullPage: true });

// Per-state close-ups so each of the three (+failed-idle) states is unambiguous.
const states = ['active', 'failed-backoff', 'failed-idle', 'no-active-run'];
for (const id of states) {
  const row = page.locator(`[data-scenario="${id}"]`);
  await row.scrollIntoViewIfNeeded();
  await row.screenshot({ path: `${OUT}/run-activity-${id}.png` });
  const label = await page.locator(`[data-testid="pill-${id}"] [data-testid="task-card-run-activity"]`).innerText().catch(() => '(none)');
  console.log(`captured ${id} -> label="${label.trim()}"`);
}

await browser.close();
console.log(`done; screenshots in ${OUT}`);
