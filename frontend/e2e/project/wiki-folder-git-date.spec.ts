import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

test('wiki folder marks the filesystem-time fallback for an untracked page', async ({ page, devBackend }, testInfo) => {
  const folderName = `e2e-wiki-local-date-${process.pid}`;
  const folderPath = path.join(devBackend.workspace, 'docs', folderName);
  mkdirSync(folderPath, { recursive: true });
  writeFileSync(path.join(folderPath, 'local-draft.md'), '# Local draft\n\nNot committed yet.\n');

  try {
    await page.route('**/api/crash-recovery/pending', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ pending: [] }),
    }));
    await page.goto('/#/projects/agent-studio-worktree/wiki', {
      waitUntil: 'domcontentloaded',
      timeout: 30_000,
    });
    const folder = page.getByTestId(`project-wiki-folder-label-${folderName}`);
    await expect(folder).toBeVisible({ timeout: 15_000 });
    await folder.click();

    await expect(page.getByTestId('wiki-folder-table')).toBeVisible();
    const marker = page.getByTestId(`wiki-folder-mtime-${folderName}/local-draft.md`);
    await expect(marker).toBeVisible();
    await expect(marker).toHaveText('*');
    await expect(marker).toHaveAttribute(
      'title',
      'Filesystem time because no Git date is available yet',
    );

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await page.getByTestId('wiki-folder-view').screenshot({
        path: testInfo.outputPath(`wiki-folder-git-dates-${theme}.png`),
      });
    }
  } finally {
    rmSync(folderPath, { recursive: true, force: true });
  }
});
