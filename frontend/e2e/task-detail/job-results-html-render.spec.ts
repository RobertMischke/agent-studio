/**
 * Beautiful HTML rendering for finished-job results.
 *
 * Plants a status.md with the full set of result-view features (sentinel
 * banner, headings, code block, diff fence, image, links) into a fixture
 * job, opens the detail view, and asserts the new renderer is wired in.
 *
 * The spec doubles as a screenshot-evidence harness: the rendered and
 * raw views are captured under `test-results/` and copied into the job
 * folder's `results/` by the task agent at the end of the run.
 */
import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

const RICH_STATUS = `# Result summary

This task **finished successfully**. Below is the polished render that the
new beautiful-results view produces.

## Highlights

- A code block with **syntax highlighting**
- A diff fence rendered as a coloured patch
- An inline image with click-to-zoom
- A sentinel banner lifted to the top

### Code

\`\`\`typescript
export function greet(name: string): string {
  return \`hello, \${name}\`;
}
\`\`\`

### Diff

\`\`\`diff
--- a/src/greet.ts
+++ b/src/greet.ts
@@ -1,3 +1,3 @@
 export function greet(name: string): string {
-  return 'hello';
+  return \`hello, \${name}\`;
 }
\`\`\`

### Reference

See [the docs site](https://example.com) for more.

[[TASK_DONE]]
`;

test.describe('Beautiful HTML result rendering', () => {
  test('finished job: rendered tab is default, sentinel banner + code block + diff visible', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `beautiful-results-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Beautiful results render test',
      targetState: '2-ready'
    });

    // Seed status.md directly. Moving to 4-auto-review lands the job in a
    // "finished" lane so the detail view defaults to Protokoll.
    {
      const created = await getJob(job.id, watchPath);
      await writeFile(join(created.folderPath, 'status.md'), RICH_STATUS);
    }
    await moveJob(job.id, watchPath, '4-auto-review');

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const protocolTab = page.getByTestId('inspector-tab-protocol');
      await expect(protocolTab).toBeVisible({ timeout: 15_000 });
      await expect(protocolTab).toHaveClass(/pane-tab--active/);

      const view = page.getByTestId('beautiful-results');
      await expect(view).toBeVisible({ timeout: 10_000 });

      // Rendered mode is the default.
      await expect(view).toHaveAttribute('data-view-mode', 'rendered');
      await expect(page.getByTestId('results-rendered')).toBeVisible();

      // Sentinel banner promoted to the top with kind=done.
      const banner = page.getByTestId('results-sentinel-banner');
      await expect(banner).toBeVisible();
      await expect(banner).toHaveAttribute('data-kind', 'done');

      // Code block decoration is present (lang label + copy button).
      await expect(page.locator('.results-code__lang').first()).toBeVisible();
      await expect(page.locator('[data-results-copy]').first()).toBeVisible();

      // Diff renders through diff2html — the d2h-* class set must appear.
      await expect(page.locator('.results-diff [class*="d2h-"]').first()).toBeVisible();

      // Headings get prose styling (different font weight from body).
      const heading = page.locator('.results-view__body h1').first();
      await expect(heading).toBeVisible();
      const headingFontWeight = await heading.evaluate(el => getComputedStyle(el).fontWeight);
      expect(Number.parseInt(headingFontWeight, 10)).toBeGreaterThanOrEqual(600);

      await page.screenshot({
        path: 'test-results/beautiful-results-rendered.png',
        fullPage: true
      });

      // Raw toggle reveals the source markdown verbatim.
      await page.getByTestId('results-view-mode-raw').click();
      const raw = page.getByTestId('results-raw');
      await expect(raw).toBeVisible();
      await expect(raw).toContainText('[[TASK_DONE]]');
      await expect(raw).toContainText('```diff');

      await page.screenshot({
        path: 'test-results/beautiful-results-raw.png',
        fullPage: true
      });

      // Toggle back to rendered.
      await page.getByTestId('results-view-mode-rendered').click();
      await expect(view).toHaveAttribute('data-view-mode', 'rendered');
      await expect(page.getByTestId('results-rendered')).toBeVisible();
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });

  test('blocked sentinel surfaces with reason text', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `beautiful-results-blocked-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Blocked render test',
      targetState: '2-ready'
    });

    {
      const created = await getJob(job.id, watchPath);
      await writeFile(
        join(created.folderPath, 'status.md'),
        '# Tried hard, hit a wall\n\nCould not access the API.\n\n[[TASK_BLOCKED:missing API key]]\n'
      );
    }
    await moveJob(job.id, watchPath, '4-auto-review');

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const banner = page.getByTestId('results-sentinel-banner');
      await expect(banner).toBeVisible({ timeout: 15_000 });
      await expect(banner).toHaveAttribute('data-kind', 'blocked');
      await expect(banner).toContainText('missing API key');
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      );
    }
  });
});
