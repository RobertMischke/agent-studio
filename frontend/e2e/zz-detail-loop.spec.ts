import { test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const out = path.resolve(__dirname, '..', 'test-results', 'detail-loop');
fs.mkdirSync(out, { recursive: true });

async function setTheme(page: any, theme: 'dark' | 'light') {
  await page.addInitScript((t: string) => {
    try { localStorage.setItem('atp.studio.theme', t); } catch {}
  }, theme);
}

async function openDetail(page: any) {
  const projBtn = page.locator('[data-testid="studio-welcome"] button.studio-welcome__project').first();
  if (await projBtn.count()) await projBtn.click().catch(() => {});
  await page.waitForTimeout(800);
  const card = page.locator('[data-testid="job-card"]').first();
  if (await card.count()) await card.click().catch(() => {});
  await page.waitForTimeout(1100);
}

async function safeShot(page: any, sel: string, file: string) {
  try {
    const el = page.locator(sel).first();
    if (await el.count() === 0) return;
    if (!(await el.isVisible().catch(() => false))) return;
    await el.screenshot({ path: file, timeout: 4000 });
  } catch {}
}

test.use({ viewport: { width: 1920, height: 1080 } });

for (const theme of ['dark', 'light'] as const) {
  test(`loop ${theme} – detail full`, async ({ page }) => {
    await setTheme(page, theme);
    await page.goto('/');
    await page.waitForTimeout(1500);
    await openDetail(page);
    await page.screenshot({ path: path.join(out, `${theme}-00-full.png`), fullPage: false });

    // Micro-regions for color/padding inspection
    await safeShot(page, 'app-protocol-pane .protocol-strip, app-protocol-pane .pane__verdict, app-protocol-pane app-hygiene-strip', path.join(out, `${theme}-12-protocol-verdict.png`));
    await safeShot(page, 'app-protocol-pane .inspector__tabs--pill', path.join(out, `${theme}-13-inspector-tabs.png`));
    await safeShot(page, '.activity-log__toolbar', path.join(out, `${theme}-14-activity-toolbar.png`));
    await safeShot(page, '[data-testid="runs-icon-row"]', path.join(out, `${theme}-15-runs-row.png`));
    await safeShot(page, '.convo-turn--system', path.join(out, `${theme}-16-system-bubble.png`));
    await safeShot(page, '.convo-turn--agent', path.join(out, `${theme}-17-agent-bubble.png`));
    await safeShot(page, '.chat-mode', path.join(out, `${theme}-18-mode-row.png`));
    await safeShot(page, '.chat-compose, app-protocol-pane textarea', path.join(out, `${theme}-19-compose.png`));
    await safeShot(page, 'app-triage-panel', path.join(out, `${theme}-90-triage.png`));
    await safeShot(page, '.studio-statusbar', path.join(out, `${theme}-99-statusbar.png`));
  });
}
