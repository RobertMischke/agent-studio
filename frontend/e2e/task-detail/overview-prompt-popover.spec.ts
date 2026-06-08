import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, waitForJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(
    `${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  ).catch(() => { /* best-effort cleanup */ });
}

function uid(suffix: string) {
  return `e2e-prompt-pop-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function openTaskDirectly(page: Page, jobId: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('prompt-tab-overview')).toBeVisible({ timeout: 10_000 });
}

/**
 * Feature (Job Details - Task Prompt modal): the Overview tab carries a
 * "Prompt" affordance next to the title that opens a centered, read-only modal
 * rendering the task prompt (promptMarkdown / prompt.md) as Markdown. It must
 * be large enough for long prompts, closable via close button / backdrop /
 * Escape, and Escape must not close the whole detail panel behind it.
 */
test.describe('Overview tab - task prompt modal', () => {
  const PROMPT_HEADING = 'Prompt modal acceptance heading';
  const PROMPT_BODY = [
    `# ${PROMPT_HEADING}`,
    '',
    'Implement the **thing** with care.',
    '',
    '- first bullet',
    '- second bullet',
    '',
    ...Array.from(
      { length: 80 },
      (_, index) => `Long prompt paragraph ${index + 1}: keep enough detail visible while the modal body scrolls.`,
    ),
  ].join('\n');

  test('trigger opens a centered read-only modal that renders long prompt markdown', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('render');
    await createJob({
      id,
      title: `Prompt modal render ${id}`,
      watchPath: wp.path,
      promptMarkdown: PROMPT_BODY,
      targetState: '1-preparation',
    });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      // Trigger lives in the Overview title block and the panel is closed by default.
      const trigger = page.getByTestId('overview-prompt-trigger');
      await expect(trigger).toBeVisible();
      await expect(page.getByTestId('overview-prompt-popover')).toHaveCount(0);

      await trigger.click();
      await expect(page.getByTestId('overview-prompt-popover-backdrop')).toBeVisible();
      const panel = page.getByTestId('overview-prompt-popover');
      await expect(panel).toBeVisible();

      // Markdown is rendered (heading becomes an <h1>, not literal "# ...").
      const body = page.getByTestId('overview-prompt-popover-body');
      await expect(body.locator('h1')).toHaveText(PROMPT_HEADING);
      await expect(body.locator('li')).toHaveCount(2);
      await expect.poll(() => body.evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true);

      const viewport = page.viewportSize() ?? await page.evaluate(() => ({
        width: window.innerWidth,
        height: window.innerHeight,
      }));
      const box = await panel.boundingBox();
      expect(box).not.toBeNull();
      if (!box) throw new Error('Prompt modal bounding box missing');
      expect(Math.abs(box.x + box.width / 2 - viewport.width / 2)).toBeLessThanOrEqual(8);
      expect(Math.abs(box.y + box.height / 2 - viewport.height / 2)).toBeLessThanOrEqual(8);
      expect(box.width).toBeCloseTo(Math.min(viewport.width * 0.9, 820), 0);
      expect(box.height).toBeLessThanOrEqual(Math.min(viewport.height * 0.8, 720) + 2);

      await page.screenshot({ path: 'test-results/overview-prompt-popover-open.png', fullPage: false });
    } finally {
      await deleteTask(id, wp.path);
    }
  });

  test('Escape closes the modal but keeps the detail panel open', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('escape');
    await createJob({
      id,
      title: `Prompt modal escape ${id}`,
      watchPath: wp.path,
      promptMarkdown: PROMPT_BODY,
      targetState: '1-preparation',
    });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      await page.getByTestId('overview-prompt-trigger').click();
      await expect(page.getByTestId('overview-prompt-popover')).toBeVisible();

      await page.keyboard.press('Escape');
      await expect(page.getByTestId('overview-prompt-popover')).toHaveCount(0);
      // The detail panel must survive the same Escape.
      await expect(page.getByTestId('detail-panes')).toBeVisible();
    } finally {
      await deleteTask(id, wp.path);
    }
  });

  test('clicking the backdrop closes it', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('outside');
    await createJob({
      id,
      title: `Prompt modal outside ${id}`,
      watchPath: wp.path,
      promptMarkdown: PROMPT_BODY,
      targetState: '1-preparation',
    });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      await page.getByTestId('overview-prompt-trigger').click();
      await expect(page.getByTestId('overview-prompt-popover')).toBeVisible();

      await page.getByTestId('overview-prompt-popover-backdrop').click({ position: { x: 5, y: 5 } });
      await expect(page.getByTestId('overview-prompt-popover')).toHaveCount(0);
    } finally {
      await deleteTask(id, wp.path);
    }
  });
});
