// Throwaway visual-evidence capture for ASS-1719 (active-button pill / no crooked underline).
// Drives the worktree dev server (4012) -> stable backend (5031) in a real browser.
import { chromium } from '@playwright/test';
import fs from 'node:fs';

const BASE = process.env.PW_BASE_URL || 'http://localhost:4012';
const OUT = process.env.OUT_DIR || 'evidence-out';
fs.mkdirSync(OUT, { recursive: true });

const assert = (cond, msg) => { if (!cond) { throw new Error('ASSERT FAILED: ' + msg); } console.log('  ok: ' + msg); };

async function dismissErrors(page) {
  for (let i = 0; i < 4; i++) {
    const overlay = page.locator('.overlay--error');
    if ((await overlay.count()) === 0) break;
    if (!(await overlay.first().isVisible({ timeout: 200 }).catch(() => false))) break;
    await page.locator('.error-dialog__close').first().click({ timeout: 1000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

const styleOf = (el) => el.evaluate((n) => {
  const s = getComputedStyle(n);
  return { color: s.color, background: s.backgroundColor, borderColor: s.borderColor, boxShadow: s.boxShadow };
});

const run = async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('console', (m) => { if (m.type() === 'error') console.log('  [browser err]', m.text().slice(0, 140)); });

  await page.addInitScript(() => {
    try {
      localStorage.setItem('atp.flag.vsCodeLayout', '1');
      localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
      localStorage.setItem('atp.theme', 'dark');
    } catch {}
  });

  console.log('Navigate', BASE);
  await page.goto(BASE + '/', { waitUntil: 'domcontentloaded' });

  // Welcome -> All projects (if shown)
  const welcome = page.getByTestId('studio-welcome');
  if ((await welcome.count()) > 0 && await welcome.first().isVisible().catch(() => false)) {
    await welcome.first().getByRole('button', { name: 'All projects' }).click({ timeout: 3000 }).catch(() => {});
  }
  await page.getByTestId('studio-board').first().waitFor({ state: 'visible', timeout: 15000 }).catch(() => {});
  await dismissErrors(page);

  assert((await page.getByTestId('studio-titlebar-chat').count()) > 0, 'vsCodeLayout studio shell is active');

  // ---- Board toggles (Lanes/Epics, Full/Compact) ----
  const cluster = page.getByTestId('studio-board-actions');
  await cluster.waitFor({ state: 'visible', timeout: 10000 });
  const epic = page.getByTestId('studio-board-epic-toggle');
  const compact = page.getByTestId('studio-board-compact-toggle');

  await page.screenshot({ path: `${OUT}/01-header-full-rest.png`, clip: { x: 0, y: 0, width: 1600, height: 84 } });
  await cluster.screenshot({ path: `${OUT}/02-board-toggles-rest.png` });

  if ((await epic.getAttribute('aria-pressed')) === 'false') await epic.click();
  if ((await compact.getAttribute('aria-pressed')) === 'false') await compact.click();
  await page.waitForTimeout(200);
  await cluster.screenshot({ path: `${OUT}/03-board-toggles-active.png` });
  await page.screenshot({ path: `${OUT}/04-header-full-active.png`, clip: { x: 0, y: 0, width: 1600, height: 84 } });

  const activeBoard = await styleOf(epic);
  const inactiveBoardRef = await styleOf(page.getByTestId('studio-board-add-task')); // primary, different
  console.log('board active style:', JSON.stringify(activeBoard));
  assert(!activeBoard.boxShadow.includes('inset'), 'board active toggle has NO inset box-shadow (no crooked underline)');
  assert(activeBoard.borderColor !== 'rgba(0, 0, 0, 0)' && activeBoard.borderColor !== inactiveBoardRef.borderColor, 'board active toggle shows a full accent border');

  // ---- Pane toggles (the icon buttons from the operator screenshot) ----
  // Open the first task card so the task tab + pane toggles render.
  const firstCard = page.locator('[data-testid^="job-card"], [data-testid^="task-card"], .studio-card, [data-testid="studio-board"] [role="button"]').first();
  let paneShown = false;
  if (await firstCard.count() > 0) {
    await firstCard.click({ timeout: 5000 }).catch(() => {});
    await dismissErrors(page);
    paneShown = await page.getByTestId('studio-pane-toggles').isVisible({ timeout: 8000 }).catch(() => false);
  }
  if (paneShown) {
    const prompt = page.getByTestId('studio-pane-toggle-prompt');
    const git = page.getByTestId('studio-pane-toggle-git');
    const paneCluster = page.getByTestId('studio-pane-toggles');
    await page.waitForTimeout(200);
    await paneCluster.screenshot({ path: `${OUT}/05-pane-toggles-active.png` });
    await page.screenshot({ path: `${OUT}/06-header-full-with-panes.png`, clip: { x: 0, y: 0, width: 1600, height: 84 } });

    const activePane = await styleOf(prompt);
    const inactivePane = await styleOf(git);
    console.log('pane active:', JSON.stringify(activePane));
    console.log('pane inactive:', JSON.stringify(inactivePane));
    assert(!activePane.boxShadow.includes('inset'), 'pane active toggle has NO inset box-shadow (crooked underline gone)');
    assert(activePane.color !== inactivePane.color, 'pane active toggle uses accent text');
    assert(activePane.borderColor !== inactivePane.borderColor, 'pane active toggle shows a full accent border (pill), inactive does not');
    assert(activePane.background !== inactivePane.background, 'pane active toggle has accent-tinted fill');
  } else {
    console.log('  WARN: pane toggles not shown (no task opened); board-toggle evidence still captured.');
  }

  await browser.close();
  console.log('DONE. paneShown=' + paneShown);
};

run().catch((e) => { console.error(e); process.exit(1); });
