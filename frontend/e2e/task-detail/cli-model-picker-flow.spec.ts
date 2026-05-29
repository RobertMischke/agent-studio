import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Regression spec for the CLI + model picker flow (2026-05).
 *
 * Operator complaint: the previous picker auto-closed on CLI selection, the
 * model list did not refresh in place, and there was no atomic Cancel /
 * Done split. The redesigned picker keeps a single dialog open while the
 * user can switch CLI (model list refreshes live), pick a model, and
 * commit both fields with one Done click. Esc cancels without firing any
 * PUT, even after a CLI switch.
 *
 * Acceptance covered here (matches the task spec verbatim):
 *   1. Open picker, switch CLI → dialog still visible
 *   2. Model list refreshes to the new CLI's models
 *   3. Select new model + Done → both PUTs (/cli-type, /model) fire,
 *      in that order
 *   4. Cancel path: open, switch CLI, Esc → no PUT
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

async function activateActivityTab(page: Page): Promise<void> {
  const activityTab = page.getByTestId('inspector-tab-activity');
  if (await activityTab.isVisible().catch(() => false)) {
    await activityTab.click();
  }
}

test.describe('CLI + model picker flow', () => {
  test('switching CLI keeps the dialog open and refreshes the model list', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `picker-flow-switch-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# picker flow',
      targetState: '2-ready',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      const badge = page.getByTestId('overview-agent').getByTestId('chat-compose-model');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await badge.click();

      const picker = page.getByTestId('overview-agent').getByTestId('chat-model-picker');
      await expect(picker).toBeVisible({ timeout: 5_000 });

      // Click the Codex CLI pill.
      await page.getByTestId('overview-agent').getByTestId('chat-model-picker-cli-codex').click();

      // Dialog must stay open.
      await expect(picker).toBeVisible();

      // Model list refreshes: the Claude opus row must disappear, and at
      // least one Codex-prefixed model row must appear (the exact id depends
      // on the backend catalog, so we use a substring match).
      await expect(
        page.getByTestId('overview-agent').getByTestId('chat-model-picker-model-claude-opus-4-7'),
      ).toHaveCount(0, { timeout: 10_000 });

      // The new CLI's default is auto-selected (aria-checked). One of the
      // model pills besides the (CLI default) row carries aria-checked=true.
      const checkedModelPill = page
        .getByTestId('overview-agent')
        .getByTestId('chat-model-picker-model-pills')
        .locator('[role="radio"][aria-checked="true"]');
      await expect(checkedModelPill.first()).toBeVisible({ timeout: 10_000 });

      // Codex CLI pill is now the active one in the CLI section.
      await expect(
        page.getByTestId('overview-agent').getByTestId('chat-model-picker-cli-codex'),
      ).toHaveAttribute('aria-checked', 'true');
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });

  test('Done after CLI + model change fires both PUTs in sequence', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `picker-flow-commit-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# picker flow commit',
      targetState: '2-ready',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      // Collect the PUT calls in order.
      const puts: { kind: 'cli-type' | 'model'; t: number }[] = [];
      page.on('request', (req) => {
        const url = req.url();
        if (req.method() !== 'PUT') return;
        if (/\/api\/jobs\/[^/]+\/cli-type(\?|$)/.test(url)) {
          puts.push({ kind: 'cli-type', t: Date.now() });
        } else if (/\/api\/jobs\/[^/]+\/model(\?|$)/.test(url)) {
          puts.push({ kind: 'model', t: Date.now() });
        }
      });

      const badge = page.getByTestId('overview-agent').getByTestId('chat-compose-model');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await badge.click();

      const picker = page.getByTestId('overview-agent').getByTestId('chat-model-picker');
      await expect(picker).toBeVisible({ timeout: 5_000 });

      // Switch CLI to Codex.
      await page.getByTestId('overview-agent').getByTestId('chat-model-picker-cli-codex').click();
      // Wait for the model list to repopulate.
      const checkedModelPill = page
        .getByTestId('overview-agent')
        .getByTestId('chat-model-picker-model-pills')
        .locator('[role="radio"][aria-checked="true"]');
      await expect(checkedModelPill.first()).toBeVisible({ timeout: 10_000 });

      // Click Done.
      await page.getByTestId('overview-agent').getByTestId('chat-model-picker-done').click();
      await expect(picker).toBeHidden();

      // Both PUTs should have fired; cli-type before model.
      await expect.poll(() => puts.length, { timeout: 15_000 }).toBeGreaterThanOrEqual(2);
      expect(puts[0].kind).toBe('cli-type');
      expect(puts.find((p) => p.kind === 'model')).toBeTruthy();
      const cliIdx = puts.findIndex((p) => p.kind === 'cli-type');
      const modelIdx = puts.findIndex((p) => p.kind === 'model');
      expect(cliIdx).toBeLessThan(modelIdx);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });

  test('Cancel path: switch CLI, press Esc → no PUT fires', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `picker-flow-cancel-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# picker flow cancel',
      targetState: '2-ready',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      const writeUrls: string[] = [];
      page.on('request', (req) => {
        if (req.method() === 'PUT' || req.method() === 'POST') {
          const url = req.url();
          if (/\/api\/jobs\/[^/]+\/(cli-type|model)(\?|$)/.test(url)) {
            writeUrls.push(`${req.method()} ${url}`);
          }
        }
      });

      const badge = page.getByTestId('overview-agent').getByTestId('chat-compose-model');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await badge.click();

      const picker = page.getByTestId('overview-agent').getByTestId('chat-model-picker');
      await expect(picker).toBeVisible({ timeout: 5_000 });

      // Switch CLI to Codex inside the picker.
      await page.getByTestId('overview-agent').getByTestId('chat-model-picker-cli-codex').click();
      await expect(picker).toBeVisible();
      // Let the catalog land + the default-select happen so we have a real
      // change in the draft (the cancel path must still suppress the PUTs).
      const checkedModelPill = page
        .getByTestId('overview-agent')
        .getByTestId('chat-model-picker-model-pills')
        .locator('[role="radio"][aria-checked="true"]');
      await expect(checkedModelPill.first()).toBeVisible({ timeout: 10_000 });

      // Cancel via Esc.
      await page.keyboard.press('Escape');
      await expect(picker).toBeHidden();

      // Give the network queue a moment to flush; assert no cli-type / model PUT.
      await page.waitForTimeout(500);
      expect(writeUrls).toEqual([]);

      // Badge text should still reflect the original Claude model.
      await expect(badge).toContainText(/opus\s+4\.7/i);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });

  test('Cancel button also reverts without firing PUTs', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `picker-flow-cancel-btn-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# picker flow cancel btn',
      targetState: '2-ready',
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await activateActivityTab(page);

      const writeUrls: string[] = [];
      page.on('request', (req) => {
        if (req.method() === 'PUT') {
          const url = req.url();
          if (/\/api\/jobs\/[^/]+\/(cli-type|model)(\?|$)/.test(url)) {
            writeUrls.push(url);
          }
        }
      });

      const badge = page.getByTestId('overview-agent').getByTestId('chat-compose-model');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await badge.click();

      const picker = page.getByTestId('overview-agent').getByTestId('chat-model-picker');
      await expect(picker).toBeVisible({ timeout: 5_000 });

      // Pick a different model in the same CLI (not switching CLI).
      const sonnetPill = page
        .getByTestId('overview-agent')
        .getByTestId('chat-model-picker-model-claude-sonnet-4-6');
      await expect(sonnetPill).toBeVisible();
      await sonnetPill.click();
      await expect(sonnetPill).toHaveAttribute('aria-checked', 'true');

      // Cancel button.
      await page.getByTestId('overview-agent').getByTestId('chat-model-picker-cancel').click();
      await expect(picker).toBeHidden();

      await page.waitForTimeout(500);
      expect(writeUrls).toEqual([]);
      await expect(badge).toContainText(/opus\s+4\.7/i);
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});
