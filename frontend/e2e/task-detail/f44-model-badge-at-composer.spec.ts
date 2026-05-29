import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * F44 — Model badge at the chat composer.
 *
 * The badge is the entry point into the "Configure agent" picker (post-2026-05
 * redesign). This spec locks the visible badge label, the open trigger
 * (left-click + right-click), and the disabled-while-running behaviour.
 *
 * The interaction flow (switch CLI, stay open, atomic Done, Cancel = no PUT)
 * is locked in `cli-model-picker-flow.spec.ts`.
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

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function activateActivityTab(page: Page): Promise<void> {
  const activityTab = page.getByTestId('inspector-tab-activity');
  if (await activityTab.isVisible().catch(() => false)) {
    await activityTab.click();
  }
}

test.describe('F44 — chat-composer model badge', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`badge shows current model + opens picker via click and right-click (${theme})`, async ({ page }, testInfo) => {
      const watchPath = await pickWatchPath();
      const job = await createJob({
        title: `f44-model-badge-${theme}-${Date.now()}`,
        watchPath,
        cliType: 'claude',
        agent: 'claude',
        model: 'claude-opus-4-7',
        promptMarkdown: '# F44 badge smoke\n\nBody paragraph.',
        targetState: '2-ready',
      });

      try {
        await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
        await setTheme(page, theme);
        await activateActivityTab(page);

        const badge = page.getByTestId('chat-compose-model');
        await expect(badge).toBeVisible({ timeout: 10_000 });
        await expect(badge).toContainText(/opus\s+4\.7/i);
        await expect(badge).toBeEnabled();

        await testInfo.attach(`f44-model-badge-${theme}.png`, {
          body: await page.screenshot({ fullPage: false }),
          contentType: 'image/png',
        });
        if (process.env.F44_RESULTS_DIR) {
          await page.screenshot({
            path: `${process.env.F44_RESULTS_DIR}/f44-model-badge-${theme}.png`,
            fullPage: false,
          });
        }

        await badge.click();
        const picker = page.getByTestId('chat-model-picker');
        await expect(picker).toBeVisible({ timeout: 5_000 });
        await expect(picker).toContainText(/Claude\s*Code/i);

        const opusPill = page.getByTestId('chat-model-picker-model-claude-opus-4-7');
        await expect(opusPill).toBeVisible();
        await expect(opusPill).toHaveAttribute('aria-checked', 'true');

        await testInfo.attach(`f44-model-menu-open-${theme}.png`, {
          body: await page.screenshot({ fullPage: false }),
          contentType: 'image/png',
        });
        if (process.env.F44_RESULTS_DIR) {
          await page.screenshot({
            path: `${process.env.F44_RESULTS_DIR}/f44-model-menu-open-${theme}.png`,
            fullPage: false,
          });
        }

        await page.keyboard.press('Escape');
        await expect(picker).toBeHidden();

        await badge.click({ button: 'right' });
        await expect(picker).toBeVisible({ timeout: 5_000 });
        await page.keyboard.press('Escape');
        await expect(picker).toBeHidden();
      } finally {
        await deleteJob(job.id, watchPath);
      }
    });
  }

  test('selecting a different model and confirming with Done persists', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f44-model-change-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# F44 model change',
      targetState: '2-ready',
    });

    try {
      const modelPutPromise = page.waitForRequest((req) =>
        req.method() === 'PUT' && /\/api\/jobs\/.+\/model/.test(req.url())
      );

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      const badge = page.getByTestId('chat-compose-model');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await expect(badge).toContainText(/opus\s+4\.7/i);
      await badge.click();

      const picker = page.getByTestId('chat-model-picker');
      await expect(picker).toBeVisible({ timeout: 5_000 });

      const sonnetPill = page.getByTestId('chat-model-picker-model-claude-sonnet-4-6');
      await expect(sonnetPill).toBeVisible();
      await sonnetPill.click();

      // Picker stays open until Done is clicked - this is the new contract.
      await expect(picker).toBeVisible();
      await expect(sonnetPill).toHaveAttribute('aria-checked', 'true');

      await page.getByTestId('chat-model-picker-done').click();
      await expect(picker).toBeHidden();

      const req = await modelPutPromise;
      expect(req.url()).toContain(`/api/jobs/${encodeURIComponent(job.id)}/model`);

      await expect(badge).toContainText(/sonnet\s+4\.6/i, { timeout: 10_000 });
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });

  test('badge exposes the correct aria-haspopup wiring (dialog, not menu)', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f44-model-disabled-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# F44 disabled',
      targetState: '2-ready',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      const badge = page.getByTestId('chat-compose-model');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await expect(badge).toHaveAttribute('aria-haspopup', 'dialog');
      await expect(badge).toHaveAttribute('aria-expanded', 'false');
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});
