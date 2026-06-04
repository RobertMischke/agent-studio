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
 * Feature (Job-Details — Task-Prompt in einem Popover anzeigen): the Overview
 * tab carries a "Prompt" affordance next to the title that opens an anchored,
 * read-only popover rendering the task prompt (promptMarkdown / prompt.md) as
 * Markdown. It must be scrollable, closable via the close button / click-outside
 * / Escape, and Escape must not close the whole detail panel behind it.
 */
test.describe('Overview tab — task prompt popover', () => {
  const PROMPT_HEADING = 'Prompt popover acceptance heading';
  const PROMPT_BODY = [
    `# ${PROMPT_HEADING}`,
    '',
    'Implement the **thing** with care.',
    '',
    '- first bullet',
    '- second bullet',
  ].join('\n');

  test('trigger opens a read-only popover that renders the prompt markdown', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('render');
    await createJob({
      id,
      title: `Prompt popover render ${id}`,
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
      const panel = page.getByTestId('overview-prompt-popover');
      await expect(panel).toBeVisible();

      // Markdown is rendered (heading becomes an <h1>, not literal "# ...").
      const body = page.getByTestId('overview-prompt-popover-body');
      await expect(body.locator('h1')).toHaveText(PROMPT_HEADING);
      await expect(body.locator('li')).toHaveCount(2);

      await page.screenshot({ path: 'test-results/overview-prompt-popover-open.png', fullPage: false });
    } finally {
      await deleteTask(id, wp.path);
    }
  });

  test('Escape closes the popover but keeps the detail panel open', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('escape');
    await createJob({
      id,
      title: `Prompt popover escape ${id}`,
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

  test('clicking outside the popover closes it', async ({ page }) => {
    const wp = await pickWatchPath();
    const id = uid('outside');
    await createJob({
      id,
      title: `Prompt popover outside ${id}`,
      watchPath: wp.path,
      promptMarkdown: PROMPT_BODY,
      targetState: '1-preparation',
    });

    try {
      await waitForJob(id, wp.path, () => true, { timeoutMs: 15_000 });
      await openTaskDirectly(page, id, wp.path);

      await page.getByTestId('overview-prompt-trigger').click();
      await expect(page.getByTestId('overview-prompt-popover')).toBeVisible();

      // Click the Status section heading — clearly outside the popover host.
      await page.getByTestId('overview-status').click({ position: { x: 5, y: 5 } });
      await expect(page.getByTestId('overview-prompt-popover')).toHaveCount(0);
    } finally {
      await deleteTask(id, wp.path);
    }
  });
});
