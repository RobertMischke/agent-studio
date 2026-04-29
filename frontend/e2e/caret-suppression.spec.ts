import { test, expect } from '@playwright/test';
import { api } from './helpers/api';
import { createJob } from './helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * Regression: clicking arbitrary non-text-input HTML elements (cards, buttons,
 * headings, body) must not leave a visible blinking text caret behind.
 * Real text-entry surfaces (`<input>`, `<textarea>`, `[contenteditable]`)
 * keep the default caret so editing still works.
 */
test.describe('caret suppression on non-text-input elements', () => {
  test('non-input clicks resolve to caret-color: transparent', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `caret-suppression-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Caret\nSome text the user might click on.',
      targetState: '2-ready'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-prompt')).toBeVisible({ timeout: 10_000 });

      const nonInputTargets = [
        'header h1',
        '.task-nav__item',
        '.pane__title',
      ];

      for (const sel of nonInputTargets) {
        const target = page.locator(sel).first();
        if (await target.count() === 0) continue;
        await target.click({ force: true }).catch(() => {});
        const caret = await page.evaluate(() => {
          const a = document.activeElement;
          if (!a) return null;
          return window.getComputedStyle(a).caretColor;
        });
        expect(caret, `caret-color after clicking ${sel}`).toBe('rgba(0, 0, 0, 0)');
      }

      // The TipTap editor must still show a caret — it's a real text surface.
      const proseMirror = page.locator('.ProseMirror').first();
      await proseMirror.click();
      const tiptapCaret = await page.evaluate(() => {
        const a = document.activeElement as HTMLElement | null;
        if (!a) return null;
        return {
          contentEditable: a.isContentEditable,
          caretColor: window.getComputedStyle(a).caretColor,
        };
      });
      expect(tiptapCaret?.contentEditable).toBe(true);
      expect(tiptapCaret?.caretColor).not.toBe('rgba(0, 0, 0, 0)');

      // Capture two screenshots for the PR description: a button-click state
      // (no caret) and the focused TipTap editor (caret still visible).
      await page.locator('.task-nav__item').first().click({ force: true });
      await page.screenshot({
        path: 'test-results/caret-after-button-click.png',
        clip: { x: 0, y: 0, width: 1280, height: 720 }
      });
      await proseMirror.click();
      await page.screenshot({
        path: 'test-results/caret-after-tiptap-focus.png',
        clip: { x: 0, y: 0, width: 1280, height: 720 }
      });
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
