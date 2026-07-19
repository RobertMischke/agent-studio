// AGT-2029 — capture real-component visual evidence for the waits-on dependency
// chip, in both themes, against the waits-on-chip mockup dev-server (4031).
// Backend-free: mounts the real TaskCardComponent and renders each dependency
// state from a seeded `TaskInfo.waitsOn`.
//
// Usage: node src/mockups/waits-on-chip/capture.mjs <outDir>
import { chromium } from 'playwright';
import * as path from 'node:path';
import * as fs from 'node:fs';

const OUT = process.argv[2] || path.resolve('playwright-screenshots/waits-on-chip');
const BASE = process.env.MOCKUP_URL || 'http://127.0.0.1:4031';
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1160, height: 900 }, deviceScaleFactor: 2 });

async function setTheme(theme) {
  await page.getByTestId(`harness-theme-${theme}`).click();
  await page.waitForTimeout(150);
}

async function shot(name) {
  await page.screenshot({ path: path.join(OUT, name), fullPage: true });
  console.log('wrote', name);
}

await page.goto(BASE, { waitUntil: 'networkidle' });
await page.locator('app-task-card').first().waitFor({ state: 'visible', timeout: 15000 });

// Assert the five dependency states each rendered a chip (open, ready, cycle,
// unknown all carry the waiting-pill; the count proves none silently dropped).
const chips = page.locator('[data-testid="task-card-waiting-on"]');
const chipCount = await chips.count();
if (chipCount !== 5) {
  throw new Error(`Expected 5 dependency chips, found ${chipCount}`);
}
const tones = await chips.evaluateAll((els) => els.map((e) => e.getAttribute('data-tone')));
console.log('chip tones:', JSON.stringify(tones));
const labels = await chips.evaluateAll((els) => els.map((e) => e.textContent.trim()));
console.log('chip labels:', JSON.stringify(labels));

for (const theme of ['dark', 'light']) {
  await setTheme(theme);
  await shot(`waits-on-chip-${theme}--mocked.png`);
}

await browser.close();
console.log('DONE');
