// ASS-1752 backend-free screenshot capture for the git-state pill harness.
// Serves from `ng serve git-state-pill-mockup` (port 4024 by default).
// Usage: node scripts/ass-1752-shots.mjs <outDir> [baseUrl]
import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';

const outDir = process.argv[2];
const baseUrl = process.argv[3] || 'http://127.0.0.1:4024';
if (!outDir) {
  console.error('usage: node scripts/ass-1752-shots.mjs <outDir> [baseUrl]');
  process.exit(2);
}
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 2 });
await page.setViewportSize({ width: 1100, height: 900 });
await page.goto(baseUrl, { waitUntil: 'networkidle' });
await page.waitForSelector('[data-testid="git-state-gallery"]');

// Full before/after gallery (all lifecycle states in one shot).
await page.locator('[data-testid="git-state-gallery"]').screenshot({
  path: `${outDir}/ass-1752-git-state-pill-before-after.png`,
});

// Per-state crops so each lifecycle row is legible on its own.
for (const id of ['A-active', 'A-reissue', 'B-landed', 'C-sequential']) {
  await page.locator(`[data-scenario="${id}"]`).screenshot({
    path: `${outDir}/ass-1752-row-${id}.png`,
  });
}

await browser.close();
console.log('screenshots written to', outDir);
