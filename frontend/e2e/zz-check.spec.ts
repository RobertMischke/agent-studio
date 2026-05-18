import { test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const out = path.resolve(__dirname, '..', 'test-results', 'check');
fs.mkdirSync(out, { recursive: true });

test.use({ viewport: { width: 1920, height: 1080 } });

for (const theme of ['dark', 'light'] as const) {
  test(`check ${theme}`, async ({ page }) => {
    await page.addInitScript((t: string) => {
      try { localStorage.setItem('atp.studio.theme', t); } catch {}
    }, theme);
    await page.goto('/');
    await page.waitForTimeout(1500);
    const projBtn = page.locator('[data-testid="studio-welcome"] button.studio-welcome__project').first();
    if (await projBtn.count()) await projBtn.click().catch(() => {});
    await page.waitForTimeout(900);
    await page.screenshot({ path: path.join(out, `${theme}-board.png`), fullPage: false });
    const card = page.locator('[data-testid="job-card"]').first();
    if (await card.count()) await card.click().catch(() => {});
    await page.waitForTimeout(1100);
    await page.screenshot({ path: path.join(out, `${theme}-detail.png`), fullPage: false });
    const sb = page.locator('[data-testid="studio-sidebar"]').first();
    if (await sb.count()) await sb.screenshot({ path: path.join(out, `${theme}-sidebar.png`) });
  });
}
