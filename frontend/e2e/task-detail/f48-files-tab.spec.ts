import { test, expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

/**
 * Mirror a single page screenshot into the job-folder `results/` directory
 * when the orchestrator passes `F48_RESULTS_DIR`. The same bytes go into
 * the Playwright report via `testInfo.attach`, but writing to disk keeps
 * the F48 acceptance-criteria screenshots co-located with the task.
 */
async function captureScreenshot(
  page: import('@playwright/test').Page,
  testInfo: import('@playwright/test').TestInfo,
  fileName: string
): Promise<void> {
  const buf = await page.screenshot({ fullPage: false });
  await testInfo.attach(fileName, { body: buf, contentType: 'image/png' });
  const dir = process.env.F48_RESULTS_DIR;
  if (dir) {
    try {
      await mkdir(dir, { recursive: true });
      await writeFile(join(dir, fileName), buf);
    } catch { /* best-effort */ }
  }
}

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

/**
 * If the auto-update-service banner ("Update failed: verification failed …")
 * is up — which happens whenever a dev backend's own /api/jobs/grouped
 * verification check ran slow on a previous boot — dismiss it so the rest
 * of the detail view is interactable. The banner is harmless to F48 but it
 * paints over the corner of the layout and would skew screenshots.
 */
async function dismissUpdateBannerIfPresent(page: import('@playwright/test').Page): Promise<void> {
  const dismiss = page.getByRole('button', { name: /^Dismiss$/ });
  if (await dismiss.count()) {
    try { await dismiss.first().click({ timeout: 1_500 }); } catch { /* best-effort */ }
  }
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
      await dismissUpdateBannerIfPresent(page);

      const tab = page.getByTestId('prompt-tab-description');
      await expect(tab).toBeVisible({ timeout: 10_000 });
      await expect(tab).toContainText(/Files/i);
      // The legacy "Description" wording must not leak — the rename is real.
      await expect(tab).not.toContainText(/Description/);

      // No badge when only prompt is present.
      await expect(page.getByTestId('prompt-tab-description-badge')).toHaveCount(0);

      // Overview is the default tab on task switch; click into Files so
      // the prompt-card / hint card mount.
      await tab.click();

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
      //
      // Brief retry so we tolerate the scanner taking a few hundred ms to
      // index the freshly created job folder on a degraded dev backend.
      let planted: Awaited<ReturnType<typeof getJob>> | null = null;
      for (let attempt = 0; attempt < 10 && planted === null; attempt++) {
        try { planted = await getJob(job.id, watchPath); }
        catch { await new Promise((r) => setTimeout(r, 500)); }
      }
      if (planted === null) {
        throw new Error(`getJob never returned for ${job.id} after retries`);
      }
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
      await dismissUpdateBannerIfPresent(page);

      // Tab badge surfaces the file count once we cross 1.
      const badge = page.getByTestId('prompt-tab-description-badge');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await expect(badge).toHaveText('5');

      // Overview is the default tab on task switch; click into Files so the
      // multi-file body mounts.
      await page.getByTestId('prompt-tab-description').click();

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
      // Articles only — the `file-card-prompt-edit`/`-cancel` buttons and the
      // `file-card-expand-<name>` expand-links inherit the same prefix.
      const cards = page.locator('article[data-testid^="file-card-"]');
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
      await captureScreenshot(page, testInfo, 'f48-files-tab-multi-collapsed-light.png');

      // Click an aspect card -> it expands and renders markdown (h1 visible).
      const aspect = page.getByTestId('file-card-aspect-code-quality.md');
      await aspect.getByText('aspect-code-quality.md').click();
      await expect(aspect).toHaveAttribute('class', /file-card--expanded/);
      await expect(aspect.locator('.markdown-body h1')).toBeVisible({ timeout: 5_000 });

      await captureScreenshot(page, testInfo, 'f48-files-tab-aspect-expanded-light.png');

      // Dark-theme screenshot — same multi-file shape, all-collapsed.
      await aspect.getByText('aspect-code-quality.md').click(); // collapse again
      await setTheme(page, 'dark');
      await captureScreenshot(page, testInfo, 'f48-files-tab-multi-collapsed-dark.png');
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
        await dismissUpdateBannerIfPresent(page);
        await setTheme(page, theme);

        // Overview is the default tab on task switch; click into Files so
        // the prompt card mounts.
        await page.getByTestId('prompt-tab-description').click();

        await expect(page.getByTestId('file-card-prompt.md')).toBeVisible({ timeout: 10_000 });
        await expect(page.getByTestId('files-pane-hint')).toBeVisible();

        await captureScreenshot(page, testInfo, `f48-files-tab-only-prompt-${theme}.png`);
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
      await dismissUpdateBannerIfPresent(page);

      // Initially the editor must not be rendered — read-only markdown only.
      await expect(page.getByTestId('prompt-editor')).toHaveCount(0);

      // Overview is the default tab on task switch; click into Files so the
      // prompt card / edit button mount.
      await page.getByTestId('prompt-tab-description').click();

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
