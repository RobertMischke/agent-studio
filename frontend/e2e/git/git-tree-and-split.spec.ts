import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

/**
 * The detail endpoint can lag the create endpoint by ~1 s while the
 * scanner cache refreshes. Poll until the lookup the frontend will issue
 * from `restoreFromUrl` succeeds, so the subsequent `page.goto(...)`
 * lands on the detail pane instead of the board view.
 */
async function waitForDetailVisible(jobId: string, watchPath: string): Promise<void> {
  const deadline = Date.now() + 10_000;
  let lastError: unknown = null;
  while (Date.now() < deadline) {
    try {
      await api(`/api/jobs/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`);
      return;
    } catch (err) {
      lastError = err;
      await new Promise(r => setTimeout(r, 200));
    }
  }
  throw new Error(`Job ${jobId} not visible via detail endpoint within 10s: ${lastError}`);
}

const SAMPLE_DIFF = `diff --git a/frontend/src/app/foo.ts b/frontend/src/app/foo.ts
index 1111111..2222222 100644
--- a/frontend/src/app/foo.ts
+++ b/frontend/src/app/foo.ts
@@ -1 +1,2 @@
-export const a = 1;
+export const a = 2;
+export const b = 3;
`;

/**
 * Status payload spans several directories so the tree has something to
 * fold ("frontend/src/app" should collapse onto one row), an aggregated
 * folder count, and a leaf at a different depth.
 */
const STATUS_PAYLOAD = {
  isRepo: true,
  branch: 'feature/tree-view',
  filesChanged: 3,
  totalAdded: 5,
  totalRemoved: 2,
  files: [
    { path: 'frontend/src/app/foo.ts', status: ' M', added: 2, removed: 1 },
    { path: 'frontend/src/app/bar.ts', status: ' M', added: 1, removed: 0 },
    { path: 'README.md',               status: ' M', added: 2, removed: 1 }
  ],
  error: null
};

/**
 * Tree view + maximized split layout regression spec. The git pane was
 * a flat <ul>; the user wants to scan a change set as a directory tree
 * and, when fullscreening, see the tree on the left and the diff on the
 * right.
 */
test.describe('Git pane — tree view and split layout', () => {
  test('renders a directory tree, folds single-child chains, and splits left/right when maximized', async ({ page }) => {
    const watch = await pickWatchPath();
    const watchPath = watch.path;
    const projectName = watch.name ?? 'Runbook';
    const job = await createJob({
      title: `git-tree-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Git tree test',
      targetState: '2-ready'
    });

    try {
      // The git pane's worktree path only renders for the project's
      // active job (otherwise: "Working-tree changes belong to whichever
      // task the agent is currently editing"). Mock the runner status so
      // our fixture becomes "active" for the project.
      await page.route('**/api/runner/status', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            projects: {
              [projectName]: {
                projectName,
                mode: 'manual',
                activeJobId: job.id,
                activeExecution: null,
                queuedJobIds: []
              }
            }
          })
        });
      });
      await page.route('**/api/jobs/*/git/status**', async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STATUS_PAYLOAD) });
      });
      await page.route('**/api/jobs/*/git/diff**', async (route) => {
        await route.fulfill({ status: 200, contentType: 'text/plain', body: SAMPLE_DIFF });
      });

      await waitForDetailVisible(job.id, watchPath);

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();

      // Tree shape: top-level rows are README.md (file) and frontend/src/app
      // (folded folder chain). Both must be present.
      const tree = page.getByTestId('git-files');
      await expect(tree.locator('[data-testid="git-tree-folder"]').filter({ hasText: 'frontend/src/app/' })).toBeVisible();
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'README.md' })).toBeVisible();

      // Folder is auto-expanded for small change sets, so leaves are visible.
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' })).toBeVisible();
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'bar.ts' })).toBeVisible();

      // Click foo.ts to load the diff.
      await tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' }).click();
      const diff = page.getByTestId('git-diff');
      await expect(diff.locator('.d2h-file-wrapper')).toBeVisible({ timeout: 10_000 });

      await page.screenshot({ path: 'e2e/_baselines/git-tree-inpane.png', fullPage: false });

      // Maximize the pane: expect tree on the left, diff on the right.
      await page.getByTestId('pane-maximize-git').click();
      const treeCol = page.getByTestId('git-tree-col');
      const diffCol = page.getByTestId('git-diff-col');
      await expect(treeCol).toBeVisible();
      await expect(diffCol).toBeVisible();

      const treeBox = await treeCol.boundingBox();
      const diffBox = await diffCol.boundingBox();
      expect(treeBox).not.toBeNull();
      expect(diffBox).not.toBeNull();
      // Tree column is to the left of the diff column.
      expect(treeBox!.x).toBeLessThan(diffBox!.x);
      // Diff column gets the lion's share of the width.
      expect(diffBox!.width).toBeGreaterThan(treeBox!.width);
      // Side-by-side diff once the pane is maximized.
      await expect(page.getByTestId('git-diff-wrap').locator('.git-view__diff-mode')).toHaveText('side-by-side');

      // Regression: in maximized split mode, the tree must stretch to fill
      // its column. The SCSS `:host([data-fill="true"]) .git-tree` rule
      // depends on the component reflecting the [fill] input onto the host.
      // When that reflection is missing, the <ul> stays capped at
      // max-height: 30vh, leaving a tall empty band under the tree.
      const treeUlBox = await tree.boundingBox();
      expect(treeUlBox).not.toBeNull();
      expect(treeUlBox!.height).toBeGreaterThan(treeBox!.height * 0.9);

      await page.screenshot({ path: 'e2e/_baselines/git-tree-split-maximized.png', fullPage: false });

      // Collapse the folded folder and confirm the leaves disappear.
      await tree.locator('[data-testid="git-tree-folder"]').filter({ hasText: 'frontend/src/app/' }).click();
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' })).toHaveCount(0);
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'README.md' })).toBeVisible();
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
