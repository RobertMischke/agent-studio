import { test, expect } from '@playwright/test';
import { mkdirSync, cpSync } from 'node:fs';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function dismissUpdateBannerIfPresent(page: import('@playwright/test').Page): Promise<void> {
  const dismiss = page.getByRole('button', { name: /^Dismiss$/ });
  if (await dismiss.count()) {
    try { await dismiss.first().click({ timeout: 1_500 }); } catch { /* best-effort */ }
  }
}

const SCREENSHOT_DIR = 'test-results';
const RESULTS_DIR = process.env.F52_RESULTS_DIR;

function copyToResults(fileName: string): void {
  if (!RESULTS_DIR) return;
  try {
    mkdirSync(RESULTS_DIR, { recursive: true });
    cpSync(join(SCREENSHOT_DIR, fileName), join(RESULTS_DIR, fileName));
  } catch { /* best-effort */ }
}

/**
 * F52: prompt-pane sub-header padding alignment, title multiline wrap,
 * and meta-row visual polish.
 */
test.describe('F52: prompt-pane sub-header padding, title wrap, meta-row polish', () => {
  test.beforeEach(async () => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
  });

  test('padding aligned, title wraps multiline, meta-row separators uniform', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const longTitle = 'F52 Very Long Title That Should Wrap Across Multiple Lines Without Being Truncated With Ellipsis Or Cut Off Mid Word In The Display';
    const job = await createJob({
      title: `f52-header-polish-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: `# ${longTitle}\n\nBody paragraph below the long title.`,
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      // Race-proof the deep-link: wait for the scanner before navigating.
      await waitForJob(job.id, watchPath, () => true, { timeoutMs: 15_000 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await dismissUpdateBannerIfPresent(page);

      // Overview is the default tab on task switch — click into Files so
      // the desc-meta strip + file cards this spec measures get mounted.
      const descTabInit = page.getByTestId('prompt-tab-description');
      await expect(descTabInit).toBeVisible({ timeout: 15_000 });
      await descTabInit.click();

      // --- Wait for the detail view to render ---
      const metaBar = page.getByTestId('desc-meta-bar');
      await expect(metaBar).toBeVisible({ timeout: 15_000 });

      // --- 1. Padding alignment ---
      const promptHeader = page.getByTestId('pane-prompt-header');
      await expect(promptHeader).toBeVisible();
      const firstTab = page.getByTestId('prompt-tab-description');
      await expect(firstTab).toBeVisible();

      const [metaBox, tabBox, headerBox] = await Promise.all([
        metaBar.boundingBox(),
        firstTab.boundingBox(),
        promptHeader.boundingBox(),
      ]);
      expect(metaBox).not.toBeNull();
      expect(tabBox).not.toBeNull();
      expect(headerBox).not.toBeNull();

      const tabLeftPad = tabBox!.x - headerBox!.x;
      const metaLeftPad = metaBox!.x - headerBox!.x;
      expect(Math.abs(tabLeftPad - metaLeftPad)).toBeLessThanOrEqual(2);

      // --- 2. Title multiline wrap ---
      const fileCard = page.getByTestId('file-card-prompt.md');
      await expect(fileCard).toBeVisible();

      const h1 = fileCard.locator('.markdown-body h1').first();
      await expect(h1).toBeVisible();

      const h1Box = await h1.boundingBox();
      expect(h1Box).not.toBeNull();
      // 1.6em * 14.5px base * 1.25 line-height ~ 29px for a single line;
      // a wrapped title must be taller.
      expect(h1Box!.height).toBeGreaterThan(29);

      const computedOverflow = await h1.evaluate(el => {
        const s = window.getComputedStyle(el);
        return {
          textOverflow: s.textOverflow,
          whiteSpace: s.whiteSpace,
        };
      });
      expect(computedOverflow.textOverflow).not.toBe('ellipsis');
      expect(computedOverflow.whiteSpace).not.toBe('nowrap');

      // --- 3. Meta-row separators ---
      const metaRow = page.getByTestId('desc-meta');
      await expect(metaRow).toBeVisible();

      const seps = metaRow.locator('.meta-row__sep');
      const sepCount = await seps.count();
      expect(sepCount).toBeGreaterThanOrEqual(2);

      const styles = await seps.evaluateAll(els =>
        els.map(el => {
          const s = window.getComputedStyle(el);
          return {
            opacity: s.opacity,
            marginLeft: s.marginLeft,
            marginRight: s.marginRight,
          };
        }),
      );
      for (let i = 1; i < styles.length; i++) {
        expect(styles[i].opacity).toBe(styles[0].opacity);
        expect(styles[i].marginLeft).toBe(styles[0].marginLeft);
        expect(styles[i].marginRight).toBe(styles[0].marginRight);
      }

      // State badge uses chip-style border-radius
      const stateBadge = metaRow.locator('.desc-meta__state');
      await expect(stateBadge).toBeVisible();
      const badgeStyle = await stateBadge.evaluate(el => {
        const s = window.getComputedStyle(el);
        return { borderRadius: s.borderRadius };
      });
      expect(badgeStyle.borderRadius).toBe('999px');

      // --- Screenshots ---
      await page.screenshot({
        path: join(SCREENSHOT_DIR, 'f52-prompt-pane-header-polish.png'),
        fullPage: false,
      });
      copyToResults('f52-prompt-pane-header-polish.png');
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
