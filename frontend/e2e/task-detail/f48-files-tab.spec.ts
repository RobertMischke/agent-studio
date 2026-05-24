import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

/**
 * F48 — "Files" tab on the task-detail prompt pane. The pane used to render
 * only prompt.md under a "Description" label; the F48 redesign surfaces
 * every `.md` in the job folder (prompt + aspect-* + *_NOTE) and labels the
 * tab "Files". The legacy testid (`prompt-tab-description`) is preserved
 * for backward-compat with older specs.
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/jobs/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch { /* best-effort cleanup */ }
}

async function setTheme(page: import('@playwright/test').Page, theme: 'light' | 'dark') {
  await page.evaluate((t) => {
    document.documentElement.setAttribute('data-studio-theme', t);
    try { window.localStorage.setItem('atp.theme', t); } catch { /* ignore */ }
  }, theme);
  await page.waitForTimeout(120);
}

test.describe('F48 Files tab — rename + only-prompt + hint', () => {
  test('tab is labeled "Files" with the legacy data-testid preserved', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-rename-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Only prompt\n\nSingle file here.',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const tab = page.getByTestId('prompt-tab-description');
      await expect(tab).toBeVisible({ timeout: 10_000 });
      await expect(tab).toContainText(/Files/i);
      // The legacy "Description" wording must not leak — the rename is real.
      await expect(tab).not.toContainText(/Description/);

      // No badge when only prompt is present.
      await expect(page.getByTestId('prompt-tab-description-badge')).toHaveCount(0);

      // Files-pane shell rendered with the prompt card auto-expanded.
      const promptCard = page.getByTestId('file-card-prompt.md');
      await expect(promptCard).toBeVisible();
      await expect(promptCard).toHaveAttribute('class', /file-card--expanded/);

      // Hint card surfaces so the user knows other .md files would appear here.
      const hint = page.getByTestId('files-pane-hint');
      await expect(hint).toBeVisible();
      await expect(hint).toContainText(/agents can drop additional/i);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});

test.describe('F48 Files tab — multi-file display', () => {
  test('aspect + note files render in the spec sort order; cards start collapsed', async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-multi-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown:
        '# Multi-file task\n\nFirst paragraph of the prompt body.\n\n' +
        'Second paragraph so the preview has something to truncate.\n',
      targetState: '2-ready'
    });

    try {
      // Plant aspect / note artifacts directly on disk — the runtime API
      // only edits prompt.md, but agents and auto-review usually drop these
      // files into the job folder themselves. This test fixture mirrors that
      // server-side state without going through the (non-existent) write
      // endpoint for them.
      const planted = await getJob(job.id, watchPath);
      await writeFile(join(planted.folderPath, 'aspect-requirement-fit.md'),
        '# requirement-fit\n\n- Does the change deliver F48?\n- Yes: prompt + aspects show.\n');
      await writeFile(join(planted.folderPath, 'aspect-code-quality.md'),
        '# code-quality\n\nNo new lint warnings; new component stays within size budgets.\n');
      await writeFile(join(planted.folderPath, 'aspect-tests-and-evidence.md'),
        '# tests\n\nUnit-test the sort + classification; Playwright covers the UI.\n');
      await writeFile(join(planted.folderPath, 'REVIEW_NOTE.md'),
        '# Review note\n\nLook at the focus-visible state on the file-card head.\n');

      await page.setViewportSize({ width: 1600, height: 1000 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // Tab badge surfaces the file count once we cross 1.
      const badge = page.getByTestId('prompt-tab-description-badge');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await expect(badge).toHaveText('5');

      // Hint card must disappear when more than one file is present.
      await expect(page.getByTestId('files-pane-hint')).toHaveCount(0);

      // Sort: prompt first, then aspects alphabetically, then notes.
      const expectedOrder = [
        'file-card-prompt.md',
        'file-card-aspect-code-quality.md',
        'file-card-aspect-requirement-fit.md',
        'file-card-aspect-tests-and-evidence.md',
        'file-card-REVIEW_NOTE.md',
      ];
      const cards = page.locator('[data-testid^="file-card-"]');
      await expect(cards).toHaveCount(expectedOrder.length);
      const seen = await cards.evaluateAll((nodes) =>
        nodes.map((n) => (n as HTMLElement).getAttribute('data-testid'))
      );
      expect(seen).toEqual(expectedOrder);

      // Every card starts collapsed in multi-file mode.
      for (const id of expectedOrder) {
        await expect(page.getByTestId(id)).toHaveAttribute('class', /file-card--collapsed/);
      }

      // Light-theme screenshot — multi-file, all collapsed (preview mode).
      await setTheme(page, 'light');
      await testInfo.attach('f48-files-tab-multi-collapsed-light.png', {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png'
      });

      // Click an aspect card -> it expands and renders markdown (h1 visible).
      const aspect = page.getByTestId('file-card-aspect-code-quality.md');
      await aspect.getByText('aspect-code-quality.md').click();
      await expect(aspect).toHaveAttribute('class', /file-card--expanded/);
      await expect(aspect.locator('.markdown-body h1')).toBeVisible({ timeout: 5_000 });

      await testInfo.attach('f48-files-tab-aspect-expanded-light.png', {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png'
      });

      // Dark-theme screenshot — same multi-file shape, all-collapsed.
      await aspect.getByText('aspect-code-quality.md').click(); // collapse again
      await setTheme(page, 'dark');
      await testInfo.attach('f48-files-tab-multi-collapsed-dark.png', {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png'
      });
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});

test.describe('F48 Files tab — only-prompt theme screenshots + Edit flow', () => {
  for (const theme of ['light', 'dark'] as const) {
    test(`only-prompt looks right in the ${theme} theme`, async ({ page }, testInfo) => {
      const watchPath = await pickWatchPath();
      const job = await createJob({
        title: `f48-only-prompt-${theme}-${Date.now()}`,
        watchPath,
        cliType: 'claude',
        agent: 'claude',
        promptMarkdown:
          '# Just the prompt\n\nThis task has only `prompt.md` in its folder. ' +
          'The hint below should explain that more files would render automatically.\n',
        targetState: '2-ready'
      });

      try {
        await page.setViewportSize({ width: 1400, height: 900 });
        await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
        await setTheme(page, theme);

        await expect(page.getByTestId('file-card-prompt.md')).toBeVisible({ timeout: 10_000 });
        await expect(page.getByTestId('files-pane-hint')).toBeVisible();

        await testInfo.attach(`f48-files-tab-only-prompt-${theme}.png`, {
          body: await page.screenshot({ fullPage: false }),
          contentType: 'image/png'
        });
      } finally {
        await deleteJob(job.id, watchPath);
      }
    });
  }

  test('Edit button on the prompt card opens the rich editor and Done returns to the rendered view', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f48-edit-prompt-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Edit me\n\nClick Edit and you should see the rich editor.',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // Initially the editor must not be rendered — read-only markdown only.
      await expect(page.getByTestId('prompt-editor')).toHaveCount(0);

      const edit = page.getByTestId('file-card-prompt-edit');
      await expect(edit).toBeVisible({ timeout: 10_000 });
      await edit.click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 5_000 });

      // Done switches back to rendered markdown.
      await page.getByTestId('file-card-prompt-cancel').click();
      await expect(page.getByTestId('prompt-editor')).toHaveCount(0);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});
