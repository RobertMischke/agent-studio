// AGT-2020 — capture real-component visual evidence for the "Delete: Hover-Icon
// → Kontextmenü" change, in both themes, against the task-card-delete mockup
// (dev-server on 4028). Backend-free: mounts the real TaskCardComponent and
// drives its real right-click context menu.
//
// Usage: node src/mockups/task-card-delete/capture.mjs <outDir>
import { chromium } from 'playwright';
import * as path from 'node:path';
import * as fs from 'node:fs';

const OUT = process.argv[2] || path.resolve('playwright-screenshots/task-card-delete');
const BASE = process.env.MOCKUP_URL || 'http://127.0.0.1:4028';
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1000, height: 620 }, deviceScaleFactor: 2 });

async function openMenuOn(testid) {
  const card = page.locator(`app-task-card:has([data-testid="${testid}"]) [data-testid="task-card"]`).first();
  await card.scrollIntoViewIfNeeded();
  await card.click({ button: 'right' });
  await page.getByTestId('card-ctx-panel').waitFor({ state: 'visible', timeout: 5000 });
  // The destructive delete row must be present + last.
  const del = page.locator('[data-testid^="card-ctx-item-delete-"]');
  await del.first().waitFor({ state: 'visible', timeout: 5000 });
}

async function dismissMenu() {
  await page.keyboard.press('Escape').catch(() => {});
  await page.locator('[data-testid="app-menu-backdrop"]').waitFor({ state: 'hidden', timeout: 3000 }).catch(() => {});
}

async function setTheme(theme) {
  await dismissMenu();
  await page.getByTestId(`harness-theme-${theme}`).click();
  await page.waitForTimeout(150);
}

async function shot(name) {
  await page.screenshot({ path: path.join(OUT, name), fullPage: false });
  console.log('wrote', name);
}

await page.goto(BASE, { waitUntil: 'networkidle' });
await page.locator('app-task-card').first().waitFor({ state: 'visible', timeout: 15000 });

// Assert the hover trash button is truly gone from the card DOM.
const trashCount = await page.locator('[data-testid="task-card-delete"], .task-card__delete').count();
if (trashCount !== 0) {
  throw new Error(`Expected no hover trash button, found ${trashCount}`);
}
console.log('OK: no hover trash button in card DOM');

for (const theme of ['dark', 'light']) {
  await setTheme(theme);

  // Task card — destructive "Delete task" at the end of the menu.
  await openMenuOn('task-card-key');
  const taskLabel = await page.getByTestId('card-ctx-item-delete-task').innerText();
  console.log(`[${theme}] task delete row label:`, JSON.stringify(taskLabel));
  await shot(`card-context-delete-task-${theme}--mocked.png`);
}
await dismissMenu();

// Epic menus: right-click the second card (epic).
async function openEpicMenu() {
  // The delete row keeps the stable id `delete-task` on every card; only the
  // label switches to "Delete epic" for an epic card.
  const epicCard = page.locator('app-task-card').nth(1).locator('[data-testid="task-card"]');
  await epicCard.click({ button: 'right' });
  await page.getByTestId('card-ctx-panel').waitFor({ state: 'visible', timeout: 5000 });
  await page.getByTestId('card-ctx-item-delete-task').waitFor({ state: 'visible', timeout: 5000 });
}

for (const theme of ['dark', 'light']) {
  await setTheme(theme);
  await openEpicMenu();
  const epicLabel = await page.getByTestId('card-ctx-item-delete-task').innerText();
  console.log(`[${theme}] epic delete row label:`, JSON.stringify(epicLabel));
  await shot(`card-context-delete-epic-${theme}--mocked.png`);
}

await browser.close();
console.log('DONE');
