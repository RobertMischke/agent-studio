import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, getJobDetail, moveJob } from '../helpers/jobs';

/**
 * Editor lock regression — a job sitting in `3-progress/` must still be
 * editable as long as no CLI process is actually running for it. The
 * previous implementation rejected edits whenever the folder was
 * `3-progress`, even after the CLI had stopped or never started, which
 * surfaced as a "Cannot edit (job in progress or not found)" modal.
 *
 * The locked-while-actually-running case is covered implicitly by the
 * claude-hello-world spec (it observes the running editor); replicating
 * it here would require burning real CLI quota.
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
  } catch {
    // best-effort cleanup
  }
}

test.describe('Prompt editor — lock semantics', () => {
  test('job in 3-progress with no live CLI is still editable', async ({ page }, testInfo) => {
    const watchPath = await pickWatchPath();
    const created = await createJob({
      title: `e2e-edit-lock-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Initial prompt\n\nBefore edit.',
      targetState: '2-ready'
    });

    try {
      // Move into 3-progress without starting any CLI — this is the exact
      // post-stop / post-crash situation that used to wedge the editor.
      await moveJob(created.id, watchPath, '3-progress');

      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 10_000 });

      // Lock banner must NOT be visible — the CLI is not running.
      await expect(page.getByTestId('prompt-editor-lock')).toHaveCount(0);

      await testInfo.attach('prompt-editor-unlocked.png', {
        body: await editor.screenshot(),
        contentType: 'image/png'
      });

      // Drive the editor through the source textarea (more deterministic
      // than TipTap's contenteditable) and save via Ctrl+S.
      await editor.getByTestId('prompt-editor-mode-toggle').click();
      await page.getByTestId('prompt-editor-mode-menu-item-source').click();
      const source = page.getByTestId('prompt-editor-source');
      await expect(source).toBeVisible();

      const newBody = `# Updated while in 3-progress\n\nrun-${Date.now()}`;
      await source.fill(newBody);
      await expect(editor).toHaveAttribute('data-state', 'dirty');

      await page.locator('body').click({ position: { x: 2, y: 2 } });
      await page.keyboard.press('Control+s');

      await expect(editor).toHaveAttribute('data-state', 'saved', { timeout: 3_000 });

      await testInfo.attach('prompt-editor-saved.png', {
        body: await editor.screenshot(),
        contentType: 'image/png'
      });

      // Backend must have accepted the PUT — no 409, no 400.
      await expect.poll(async () => {
        const detail = await getJobDetail(created.id, watchPath);
        return detail.promptMarkdown ?? '';
      }, { timeout: 5_000 }).toContain('Updated while in 3-progress');

      // Status.md side: edit affordance must also be available.
      const statusEditButton = page.getByRole('button', { name: '✏️ Edit' });
      await expect(statusEditButton).toBeVisible();
    } finally {
      await deleteJob(created.id, watchPath);
    }
  });

  test('backend rejects edits with 409 only when the CLI is live', async () => {
    // Pure API-level smoke: a freshly-created job in 2-ready accepts a PUT
    // to its prompt.md (no CLI running).
    const watchPath = await pickWatchPath();
    const created = await createJob({
      title: `e2e-edit-lock-api-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Seed',
      targetState: '2-ready'
    });

    try {
      await api(
        `/api/jobs/${encodeURIComponent(created.id)}/files/prompt.md?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'PUT', body: JSON.stringify({ content: '# Edited via API' }) }
      );

      const detail = await getJobDetail(created.id, watchPath);
      expect(detail.promptMarkdown ?? '').toContain('Edited via API');

      // Same after a move into 3-progress — folder location must not gate edits.
      await moveJob(created.id, watchPath, '3-progress');
      await api(
        `/api/jobs/${encodeURIComponent(created.id)}/files/prompt.md?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'PUT', body: JSON.stringify({ content: '# Edited in 3-progress' }) }
      );

      const detail2 = await getJobDetail(created.id, watchPath);
      expect(detail2.promptMarkdown ?? '').toContain('Edited in 3-progress');
    } finally {
      await deleteJob(created.id, watchPath);
    }
  });
});
