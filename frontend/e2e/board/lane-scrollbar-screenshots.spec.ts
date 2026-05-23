import { test, expect, Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * F28 screenshot evidence — capture the board with the new
 * scrollbar-gutter contract in both themes so a reviewer can compare
 * against the pre-fix screenshot in the task prompt.
 *
 * Not a regression test; only writes PNGs to JOB_RESULTS_DIR when set
 * (or test-results/ otherwise). The functional contract is in
 * lane-scrollbar.spec.ts.
 */

async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  const studio = page.getByTestId('studio-board');
  const legacy = page.getByTestId('kanban-dashboard');
  const welcome = page.getByTestId('studio-welcome');
  await Promise.race([
    studio.first().waitFor({ state: 'visible', timeout: 8_000 }),
    legacy.first().waitFor({ state: 'visible', timeout: 8_000 }),
    welcome.first().waitFor({ state: 'visible', timeout: 8_000 }),
  ]).catch(() => { /* nothing */ });

  if ((await welcome.count()) > 0 && (await welcome.first().isVisible().catch(() => false))) {
    const allProjects = welcome.first().getByRole('button', { name: 'All projects' });
    await allProjects.click({ timeout: 3_000 }).catch(() => { /* nothing */ });
    await studio.first().waitFor({ state: 'visible', timeout: 5_000 }).catch(() => { /* nothing */ });
  }

  await expect(page.locator('.column__body').first()).toBeVisible({ timeout: 10_000 });
}

function resolveOutDir(): string {
  const job = process.env.JOB_RESULTS_DIR;
  if (job && job.trim().length > 0) {
    const dir = path.join(job, 'f28-lane-scrollbar');
    fs.mkdirSync(dir, { recursive: true });
    return dir;
  }
  const fallback = path.join('test-results', 'f28-lane-scrollbar');
  fs.mkdirSync(fallback, { recursive: true });
  return fallback;
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.setAttribute('data-studio-theme', t);
  }, theme);
}

test.describe('F28 — board screenshots in both themes', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`board lanes — ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 900 });
      await gotoBoard(page);
      await setTheme(page, theme);
      // Give the theme flip a frame to settle.
      await page.waitForTimeout(120);

      const outDir = resolveOutDir();
      const file = path.join(outDir, `board-${theme}.png`);
      await page.screenshot({ path: file, fullPage: false });
      expect(fs.existsSync(file), `screenshot landed at ${file}`).toBe(true);
    });
  }
});
