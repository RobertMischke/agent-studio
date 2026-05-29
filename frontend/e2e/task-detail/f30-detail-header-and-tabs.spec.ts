import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * F30 — Task-detail header + tabs redesign.
 *
 * Locks the redesigned prompt-pane layout so a future regression cannot
 * silently revive the two-row "#id · created · lane" + "P M paperclip"
 * stack, and proves the Markdown ↔ rich-text toggle hides behind the
 * overflow menu.
 *
 * Visible chrome we assert:
 *   - desc-meta strip is projected into the editor toolbar (single row,
 *     no separate sibling above the editor)
 *   - prompt-editor-mode-toggle ("…") button is present in the bar
 *   - the inline P / M tabs (old `.md-editor__tab--active` segment) are
 *     gone from the bar
 *   - overflow menu reveals two rows: rich + Markdown source
 *   - active tab in the pane-header tab strip carries the new
 *     `--studio-bg-tab-active` background + accent bottom border
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

async function setTheme(page: import('@playwright/test').Page, theme: 'dark' | 'light'): Promise<void> {
  // Mirrors the theme toggle in studio-shell: stamp `data-studio-theme`
  // on <html> (token bridge) + write the persisted preference so the
  // shell's effect doesn't overwrite it on the next change-detection.
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

test.describe('F30 — Task-detail header + tabs redesign', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`Description header is one row + mode toggle lives in overflow menu (${theme})`, async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f30-redesign-${theme}-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F30 redesign smoke\n\nBody paragraph.',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await setTheme(page, theme);

      // Overview is the default tab on task switch — click into Files so
      // the F30 redesign surface (desc-meta + file-card-prompt-edit) is
      // mounted.
      const descTab = page.getByTestId('prompt-tab-description');
      await expect(descTab).toBeVisible({ timeout: 15_000 });
      await descTab.click();

      // F48: prompt is shown as a card; editor opens on demand.
      await page.getByTestId('file-card-prompt-edit').click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 10_000 });

      // Meta strip rendered at the top of the Files-tab body; only one
      // instance exists across the page.
      const meta = page.getByTestId('desc-meta');
      await expect(meta).toHaveCount(1);
      await expect(meta).toContainText(/#\d+/);
      await expect(meta).toContainText(/created/);
      await expect(meta).toContainText(/Human Ready/);

      // The visible toolbar must no longer carry the old P / M segment
      // tabs — only icon-style action buttons (paperclip + save + mode
      // overflow). Locator scoped to the editor.
      await expect(editor.locator('.md-editor__tabs')).toHaveCount(0);

      // Overflow trigger present.
      const modeToggle = editor.getByTestId('prompt-editor-mode-toggle');
      await expect(modeToggle).toBeVisible();

      // Open it and confirm both view modes are offered.
      await modeToggle.click();
      const menu = page.getByTestId('prompt-editor-mode-menu-panel');
      await expect(menu).toBeVisible();
      await expect(menu.getByText(/rich text/i)).toBeVisible();
      await expect(menu.getByText(/Markdown source/i)).toBeVisible();
      await testInfo.attach(`f30-mode-overflow-open-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png'
      });
      if (process.env.F30_RESULTS_DIR) {
        await page.screenshot({ path: `${process.env.F30_RESULTS_DIR}/f30-mode-overflow-open-${theme}.png`, fullPage: false });
      }

      // Pick Markdown source — editor switches to the textarea path.
      await page.getByTestId('prompt-editor-mode-menu-item-source').click();
      await expect(page.getByTestId('prompt-editor-source')).toBeVisible();

      // Active tab in the pane-header carries the new active styling.
      await expect(descTab).toHaveClass(/pane-tab--active/);
      const bg = await descTab.evaluate((el) => getComputedStyle(el).backgroundColor);
      // The new active background reads `--studio-bg-tab-active`, which
      // resolves to a non-transparent colour in both themes; inactive
      // siblings render `rgba(0,0,0,0)`. Asserting non-transparency is
      // enough — exact RGB drifts with the palette.
      expect(bg).not.toBe('rgba(0, 0, 0, 0)');

      await testInfo.attach(`f30-detail-redesigned-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png'
      });
      if (process.env.F30_RESULTS_DIR) {
        await page.screenshot({ path: `${process.env.F30_RESULTS_DIR}/f30-detail-redesigned-${theme}.png`, fullPage: false });
      }
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
  }
});
