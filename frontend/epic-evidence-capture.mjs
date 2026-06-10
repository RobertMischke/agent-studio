import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';

const BASE = process.env.EVID_BASE_URL ?? 'http://127.0.0.1:4026';
const OUT = process.env.EVID_OUT;
if (!OUT) {
  console.error('EVID_OUT (absolute results dir) is required');
  process.exit(2);
}
mkdirSync(OUT, { recursive: true });

const TOGGLE = '[data-testid="task-card-epic-toggle"]';
const PANEL = '[data-testid="task-card-epic-subtasks"]';
const ITEM = '[data-testid="task-card-epic-subtask"]';
const REFRESH = '[data-testid="harness-refresh"]';

const failures = [];
function check(label, cond) {
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${label}`);
  if (!cond) failures.push(label);
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 520, height: 760 }, deviceScaleFactor: 2 });
page.on('console', (m) => { if (m.type() === 'error') console.log('  [browser-error]', m.text()); });

await page.goto(BASE, { waitUntil: 'networkidle' });
await page.waitForSelector('app-task-card', { timeout: 30_000 });
await page.waitForSelector(TOGGLE, { timeout: 30_000 });

const stage = page.locator('.harness__stage');

// 1) Collapsed (initial)
const ariaCollapsed = await page.getAttribute(TOGGLE, 'aria-expanded');
check('toggle starts collapsed (aria-expanded != true)', ariaCollapsed !== 'true');
check('no sub-list rendered while collapsed', (await page.locator(PANEL).count()) === 0);
await stage.screenshot({ path: `${OUT}/01-collapsed.png` });

// 2) Expand on user click
await page.click(TOGGLE);
await page.waitForSelector(PANEL, { state: 'visible', timeout: 5_000 });
await page.waitForTimeout(350); // let the 160ms calm-reveal settle
check('toggle now expanded (aria-expanded == true)', (await page.getAttribute(TOGGLE, 'aria-expanded')) === 'true');
const itemCount = await page.locator(ITEM).count();
check('three sub-tasks rendered', itemCount === 3);
await stage.screenshot({ path: `${OUT}/02-expanded.png` });

// Stamp live DOM nodes to prove they are reused (not rebuilt) across a refresh.
await page.evaluate(({ panel, item }) => {
  const p = document.querySelector(panel);
  if (p) p.dataset.persistMarker = 'panel';
  document.querySelectorAll(item).forEach((el, i) => { el.dataset.persistMarker = `item-${i}`; });
}, { panel: PANEL, item: ITEM });

// 3) Simulate a board poll refresh: brand-new TaskInfo objects, same ids.
await page.click(REFRESH);
await page.waitForTimeout(250);
check('refresh count incremented', (await page.textContent('[data-testid="harness-refresh-count"]'))?.includes('1'));
check('STILL expanded after refresh (aria-expanded == true)', (await page.getAttribute(TOGGLE, 'aria-expanded')) === 'true');
check('exactly one sub-list panel (no double mount)', (await page.locator(PANEL).count()) === 1);
check('still exactly three sub-tasks', (await page.locator(ITEM).count()) === 3);
const markers = await page.evaluate(({ panel, item }) => {
  const p = document.querySelector(panel);
  const items = Array.from(document.querySelectorAll(item)).map((el) => el.dataset.persistMarker ?? null);
  return { panel: p?.dataset.persistMarker ?? null, items };
}, { panel: PANEL, item: ITEM });
check('panel DOM node reused across refresh (marker survived)', markers.panel === 'panel');
check('sub-task DOM nodes reused across refresh (markers survived)',
  markers.items.length === 3 && markers.items.every((m) => m && m.startsWith('item-')));
await stage.screenshot({ path: `${OUT}/03-after-refresh-still-expanded.png` });

// 4) A few more refreshes to mimic continuous polling — must stay open.
for (let i = 0; i < 3; i++) { await page.click(REFRESH); await page.waitForTimeout(120); }
check('still expanded after 4 total refreshes', (await page.getAttribute(TOGGLE, 'aria-expanded')) === 'true');
check('still one panel after repeated polls', (await page.locator(PANEL).count()) === 1);
await stage.screenshot({ path: `${OUT}/04-after-repeated-refresh.png` });

// 5) User can still collapse it deliberately.
await page.click(TOGGLE);
await page.waitForTimeout(200);
check('user can still collapse (aria-expanded != true)', (await page.getAttribute(TOGGLE, 'aria-expanded')) !== 'true');
check('sub-list removed on collapse', (await page.locator(PANEL).count()) === 0);
await stage.screenshot({ path: `${OUT}/05-collapsed-again.png` });

await browser.close();

console.log(`\nartifacts written to: ${OUT}`);
if (failures.length) {
  console.error(`\n${failures.length} CHECK(S) FAILED:\n - ${failures.join('\n - ')}`);
  process.exit(1);
}
console.log('\nALL CHECKS PASSED');
