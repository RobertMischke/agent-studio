import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

const SAMPLE_DIFF = `diff --git a/src/example.ts b/src/example.ts
index 1111111..2222222 100644
--- a/src/example.ts
+++ b/src/example.ts
@@ -1,5 +1,6 @@
 export function greet(name: string) {
-  return 'Hello, ' + name;
+  // diff2html renders this with proper add/remove highlighting.
+  return \`Hello, \${name}!\`;
 }

 export const VERSION = '1.0.0';
`;

const STATUS_PAYLOAD = {
  isRepo: true,
  branch: 'feature/diff-viewer',
  filesChanged: 1,
  totalAdded: 2,
  totalRemoved: 1,
  files: [
    { path: 'src/example.ts', status: ' M', added: 2, removed: 1 }
  ],
  error: null
};

/**
 * Verifies the new diff2html-based diff viewer:
 *  - replaces the plain <pre> with a syntax-coloured diff,
 *  - exposes a per-diff maximize toggle that fills the viewport,
 *  - switches from line-by-line to side-by-side when maximized.
 *
 * The git endpoints are stubbed so the test is independent of the
 * actual working-tree state of the watched repo.
 */
test.describe('Git pane — diff viewer + maximize', () => {
  test('renders diff2html output and toggles fullscreen maximize', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `diff-viewer-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Diff viewer test',
      targetState: '2-ready'
    });

    try {
      await page.route('**/api/tasks/*/git/status**', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(STATUS_PAYLOAD)
        });
      });

      await page.route('**/api/tasks/*/git/diff**', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'text/plain',
          body: SAMPLE_DIFF
        });
      });

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });

      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();
      await expect(page.getByTestId('git-files-count')).toHaveText('1 files');

      // Select the file leaf in the tree to load the diff. The tree renders
      // src/ as a folder and example.ts as a leaf below it; click the leaf.
      await page.getByTestId('git-files').locator('[data-testid="git-tree-file"]').filter({ hasText: 'example.ts' }).click();

      // diff2html injects classes like d2h-file-wrapper / d2h-code-line — assert
      // the viewer rendered structurally instead of pinning specific text.
      const diff = page.getByTestId('git-diff');
      await expect(diff.locator('.d2h-file-wrapper')).toBeVisible({ timeout: 10_000 });
      await expect(diff.locator('.d2h-ins').first()).toBeVisible();
      await expect(diff.locator('.d2h-del').first()).toBeVisible();

      // In-pane mode is line-by-line.
      const wrap = page.getByTestId('git-diff-wrap');
      await expect(wrap).not.toHaveClass(/git-view__diff-wrap--maximized/);
      await expect(wrap.locator('.git-view__diff-mode')).toHaveText('line-by-line');

      await page.screenshot({ path: 'e2e/_baselines/git-diff-inpane.png', fullPage: false });

      // Maximize the diff — should fill the viewport and switch to side-by-side.
      await page.getByTestId('git-diff-maximize').click();
      await expect(wrap).toHaveClass(/git-view__diff-wrap--maximized/);
      await expect(wrap.locator('.git-view__diff-mode')).toHaveText('side-by-side');
      await expect(diff.locator('.d2h-file-side-diff').first()).toBeVisible({ timeout: 10_000 });

      // Bounding box should fill (roughly) the viewport.
      const box = await wrap.boundingBox();
      expect(box).not.toBeNull();
      const viewport = page.viewportSize();
      expect(viewport).not.toBeNull();
      expect(box!.width).toBeGreaterThan(viewport!.width * 0.95);
      expect(box!.height).toBeGreaterThan(viewport!.height * 0.95);

      await page.screenshot({ path: 'e2e/_baselines/git-diff-maximized.png', fullPage: false });

      // Restore back to in-pane mode.
      await page.getByTestId('git-diff-maximize').click();
      await expect(wrap).not.toHaveClass(/git-view__diff-wrap--maximized/);
      await expect(wrap.locator('.git-view__diff-mode')).toHaveText('line-by-line');
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
