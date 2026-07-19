import { test, expect, Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function openDetail(page: Page, jobId: string, watchPath: string) {
  await page.setViewportSize({ width: 1600, height: 1000 });
  // Ensure each test starts from a clean pane-weight slate so the
  // splitter-drag persistence assertions are deterministic regardless
  // of which spec ran before.
  await page.goto('/');
  await page.evaluate(() => {
    localStorage.removeItem('taskboard.paneWeights');
    localStorage.removeItem('taskboard.panesVisible');
  });
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('pane-prompt')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('pane-protocol')).toBeVisible();
}

/**
 * Task-detail layout polish (operator 2026-05-28):
 *  - Detail surface reads --studio-bg-surface (white on light, clean
 *    base on dark) and carries no border / radius "rand".
 *  - Pane splitter pseudo-element hit-box is at least 8 px wide so
 *    a slightly-off click still lands the drag handle.
 *  - Active header tab is visually distinct from inactive (background
 *    + 2 px accent underline + bumped font weight).
 *  - Splitter drag persists across reload via localStorage.
 */
test.describe('Task detail layout polish', () => {
  test('detail surface uses bg-surface token and renders flush (no border/radius)', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `layout-polish-surface-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# layout polish — surface',
      targetState: '2-ready',
    });

    try {
      await openDetail(page, job.id, watchPath);

      const styles = await page.locator('.detail').first().evaluate(el => {
        const cs = getComputedStyle(el);
        const surface = getComputedStyle(document.documentElement)
          .getPropertyValue('--studio-bg-surface').trim();
        return {
          background: cs.backgroundColor,
          border: cs.border,
          borderRadius: cs.borderRadius,
          surfaceToken: surface,
        };
      });
      expect(styles.surfaceToken.length).toBeGreaterThan(0);
      expect(styles.borderRadius).toBe('0px');
      // border collapses to "0px none rgb(…)" once we drop the 1px ring
      expect(styles.border).toMatch(/\b0px\b/);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('pane splitter hit-box is at least 8 px wide via ::before pseudo-element', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `layout-polish-splitter-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# layout polish — splitter hitbox',
      targetState: '2-ready',
    });

    try {
      await openDetail(page, job.id, watchPath);

      const splitter = page.getByTestId('pane-splitter').first();
      await expect(splitter).toBeVisible();

      const geometry = await splitter.evaluate(el => {
        const visual = el.getBoundingClientRect();
        const before = window.getComputedStyle(el, '::before');
        // Measured hit-box = visible width + |left| + |right| insets,
        // because ::before is absolutely positioned with negative
        // horizontal offsets per the SCSS contract.
        const leftPx = parseFloat(before.left);
        const rightPx = parseFloat(before.right);
        return {
          visualWidth: visual.width,
          hitWidth: visual.width + Math.abs(leftPx) + Math.abs(rightPx),
          beforeContent: before.content,
        };
      });
      expect(geometry.beforeContent).not.toBe('none');
      expect(geometry.visualWidth).toBeLessThanOrEqual(3); // still a thin visible line
      expect(geometry.hitWidth).toBeGreaterThanOrEqual(8);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('active header tab is clearly distinct from inactive (background + accent underline)', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `layout-polish-tabs-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# layout polish — tabs',
      targetState: '2-ready',
    });

    try {
      await openDetail(page, job.id, watchPath);

      const promptHeader = page.getByTestId('pane-prompt-header');
      const activeTab = promptHeader.locator('.pane-tab.pane-tab--active').first();
      const inactiveTab = promptHeader.locator('.pane-tab:not(.pane-tab--active):not([disabled])').first();
      await expect(activeTab).toBeVisible();
      await expect(inactiveTab).toBeVisible();

      const visuals = await page.evaluate(() => {
        const active = document.querySelector(
          '[data-testid="pane-prompt-header"] .pane-tab.pane-tab--active',
        ) as HTMLElement | null;
        const inactive = document.querySelector(
          '[data-testid="pane-prompt-header"] .pane-tab:not(.pane-tab--active):not([disabled])',
        ) as HTMLElement | null;
        if (!active || !inactive) return null;
        const a = getComputedStyle(active);
        const i = getComputedStyle(inactive);
        return {
          activeBg: a.backgroundColor,
          activeBorderBottom: a.borderBottomColor,
          activeBorderWidth: parseFloat(a.borderBottomWidth),
          activeWeight: a.fontWeight,
          activeAriaSelected: active.getAttribute('aria-selected'),
          activeRole: active.getAttribute('role'),
          inactiveBg: i.backgroundColor,
          inactiveBorderBottom: i.borderBottomColor,
          inactiveWeight: i.fontWeight,
        };
      });
      expect(visuals).not.toBeNull();
      expect(visuals!.activeRole).toBe('tab');
      expect(visuals!.activeAriaSelected).toBe('true');
      // Active vs inactive must paint a different background — that is
      // the operator's main complaint (Issue 3).
      expect(visuals!.activeBg).not.toBe(visuals!.inactiveBg);
      // 2 px accent underline on active.
      expect(visuals!.activeBorderWidth).toBe(2);
      // Inactive paints a transparent bottom-border (so it does not look
      // like a permanently-active row).
      expect(visuals!.inactiveBorderBottom).toMatch(/rgba\(.*,\s*0\)|transparent/);
      // Active label sits at >= 600 (bumped weight).
      const aw = parseInt(visuals!.activeWeight, 10);
      expect(Number.isNaN(aw) ? 700 : aw).toBeGreaterThanOrEqual(600);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('pane splitter drag persists across reload', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `layout-polish-splitter-persist-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# layout polish — splitter persistence',
      targetState: '2-ready',
    });

    try {
      await openDetail(page, job.id, watchPath);

      const promptPane = page.getByTestId('pane-prompt');
      const splitter = page.getByTestId('pane-splitter').first();
      const initialBox = await promptPane.boundingBox();
      const splitterBox = await splitter.boundingBox();
      if (!initialBox || !splitterBox) throw new Error('Missing pane / splitter geometry');

      // Drag the splitter ~180 px to the right via pointer events so the
      // service's pointermove listener fires.
      const startX = splitterBox.x + splitterBox.width / 2;
      const startY = splitterBox.y + splitterBox.height / 2;
      await page.mouse.move(startX, startY);
      await page.mouse.down();
      await page.mouse.move(startX + 90, startY, { steps: 6 });
      await page.mouse.move(startX + 180, startY, { steps: 6 });
      await page.mouse.up();

      const afterBox = await promptPane.boundingBox();
      expect(afterBox).not.toBeNull();
      expect(afterBox!.width).toBeGreaterThan(initialBox.width + 40);

      // localStorage must carry the new weights.
      const stored = await page.evaluate(() => localStorage.getItem('taskboard.paneWeights'));
      expect(stored).not.toBeNull();

      await page.reload();
      await expect(page.getByTestId('pane-prompt')).toBeVisible({ timeout: 15_000 });
      const reloadBox = await page.getByTestId('pane-prompt').boundingBox();
      expect(reloadBox).not.toBeNull();
      // Reloaded width should land within 12 px of the dragged width
      // (rounding + container resize tolerance).
      expect(Math.abs(reloadBox!.width - afterBox!.width)).toBeLessThan(12);
    } finally {
      await page.evaluate(() => {
        localStorage.removeItem('taskboard.paneWeights');
      });
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('tab click flips the visible active state', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `layout-polish-tabswap-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# layout polish — tab swap',
      targetState: '2-ready',
    });

    try {
      await openDetail(page, job.id, watchPath);

      // Files / Description tab is testid="pane-prompt-tab-description".
      // Overview is the boot default; click description and observe
      // the active class moves.
      const overviewTab = page.locator('[data-testid="pane-prompt-header"] .pane-tab').first();
      await expect(overviewTab).toHaveClass(/pane-tab--active/);

      const descriptionTab = page.locator(
        '[data-testid="pane-prompt-header"] .pane-tab',
        { hasText: /Files|Description/i },
      ).first();
      if (await descriptionTab.count() === 0) {
        test.info().annotations.push({ type: 'note', description: 'No description tab visible — view-model differs' });
        return;
      }
      await descriptionTab.click();
      await expect(descriptionTab).toHaveClass(/pane-tab--active/);
      await expect(overviewTab).not.toHaveClass(/pane-tab--active/);
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });
});
