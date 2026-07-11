// AGT-2069 — capture real-component visual evidence for planning-task
// visibility, in both themes, against the planning-visibility mockup dev-server
// (4032). Backend-free: mounts the real TaskCardComponent, the AGT-2050
// TaskReferenceMicrocardComponent, and the PlanningSpawnPanelComponent and
// renders each state from a seeded `TaskInfo.mode` / `TaskInfo.planningSpawn`.
//
// Usage: node src/mockups/planning-visibility/capture.mjs <outDir>
import { chromium } from 'playwright';
import * as path from 'node:path';
import * as fs from 'node:fs';

const OUT = process.argv[2] || path.resolve('playwright-screenshots/planning-visibility');
const BASE = process.env.MOCKUP_URL || 'http://127.0.0.1:4032';
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1160, height: 1200 }, deviceScaleFactor: 2 });

async function setTheme(theme) {
  await page.getByTestId(`harness-theme-${theme}`).click();
  await page.waitForTimeout(150);
}

async function shot(name) {
  await page.screenshot({ path: path.join(OUT, name), fullPage: true });
  console.log('wrote', name);
}

await page.goto(BASE, { waitUntil: 'networkidle' });
await page.locator('app-task-card').first().waitFor({ state: 'visible', timeout: 20000 });
await page.locator('app-planning-spawn-panel').first().waitFor({ state: 'visible', timeout: 20000 });

// A) The planning + research cards each render a mode pill; the coding card does not.
const modePills = page.locator('[data-testid="task-card-mode"]');
const modeCount = await modePills.count();
if (modeCount !== 2) {
  throw new Error(`Expected 2 mode pills (planning + research), found ${modeCount}`);
}
const modes = await modePills.evaluateAll((els) => els.map((e) => e.getAttribute('data-mode')));
console.log('card mode pills:', JSON.stringify(modes));

// B) The AGT-2050 microcard rendered.
const microcards = await page.locator('app-task-reference-microcard').count();
if (microcards < 1) throw new Error('Expected at least one spawn microcard');

// B + contract) Three spawn panels; one loud "no follow-ups" warning; two contract-met.
const panels = await page.locator('[data-testid="planning-spawn-panel"]').count();
if (panels !== 3) throw new Error(`Expected 3 spawn panels, found ${panels}`);
const warnings = await page.locator('[data-testid="planning-no-followups-warning"]').count();
if (warnings !== 1) throw new Error(`Expected exactly 1 no-follow-up warning, found ${warnings}`);
const contracts = await page.locator('[data-testid="planning-contract"]').allTextContents();
console.log('contract badges:', JSON.stringify(contracts.map((c) => c.trim())));

for (const theme of ['dark', 'light']) {
  await setTheme(theme);
  await shot(`planning-visibility-${theme}--mocked.png`);
}

await browser.close();
console.log('DONE');
