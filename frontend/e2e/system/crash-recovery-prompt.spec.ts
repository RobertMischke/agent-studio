import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { expect, test } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

test.describe('Crash recovery prompt', () => {
  test('shows pending startup recovery items and commits only after confirmation', async ({ page, devBackend }) => {
    await expect.poll(async () => (await page.request.get(`${devBackend.baseUrl}/healthz`)).ok()).toBe(true);
    let commitCalled = false;

    await page.route('**/api/crash-recovery/pending', async (route) => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({
        json: {
          pending: [
            {
              id: 'pending-1',
              createdAt: '2026-06-23T10:00:00Z',
              projectName: 'Agent Taskboard',
              jobId: 'AGT-1807',
              repoRoot: 'C:/Projects/agent-taskboard-devspace/agent-taskboard-dev',
              files: ['src/app/example.ts', 'backend/Features/Runner/CrashRecoveryService.cs'],
              message: 'chore(crash-recovery): rescue orphan changes for AGT-1807\n\nRecovered changes.',
              reason: 'Uncommitted changes were found at startup and attributed to AGT-1807.',
              classification: 'review-required',
            },
          ],
        },
      });
    });

    await page.route('**/api/crash-recovery/pending/pending-1/commit', async (route) => {
      commitCalled = true;
      await route.fulfill({
        json: {
          status: 'committed',
          pending: null,
          commitSha: 'abc1234',
          error: null,
        },
      });
    });

    await page.goto('/');

    const dialog = page.getByTestId('crash-recovery-prompt');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText('Review recovered working-tree changes')).toBeVisible();
    await expect(dialog.getByText('Agent Taskboard')).toBeVisible();
    await expect(dialog.getByText('AGT-1807', { exact: true })).toBeVisible();
    await expect(dialog.getByText('src/app/example.ts')).toBeVisible();

    await dialog.getByTestId('crash-recovery-commit').click();
    await expect(dialog).toBeHidden();
    expect(commitCalled).toBe(true);
  });

  test('keeps the board interactive while unattributed read-evidence sidecars stay uncommitted', async ({ page, devBackend }) => {
    await expect.poll(async () => (await page.request.get(`${devBackend.baseUrl}/healthz`)).ok()).toBe(true);
    const dismissed: string[] = [];
    await page.route('**/api/crash-recovery/pending', async (route) => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({
        json: {
          pending: [
            {
              id: 'runner-sidecars',
              createdAt: '2026-07-30T10:00:00Z',
              projectName: 'Coding Agent Runner',
              jobId: null,
              repoRoot: '/workspace/coding-agent-runner',
              files: [
                'docs/system/runner.md.meta.json',
                'docs/operations/setup.md.meta.json',
                'docs/quality/runtime.md.meta.json',
              ],
              message: 'chore(crash-recovery): rescue orphan changes for project Coding Agent Runner',
              reason: 'Uncommitted changes were found at startup with no active job attribution.',
              classification: 'trivial',
            },
            {
              id: 'chat-sidecars',
              createdAt: '2026-07-30T10:00:00Z',
              projectName: 'Coding Agent Chat',
              jobId: null,
              repoRoot: '/workspace/coding-agent-chat',
              files: ['docs/README.md.meta.json'],
              message: 'chore(crash-recovery): rescue orphan changes for project Coding Agent Chat',
              reason: 'Uncommitted changes were found at startup with no active job attribution.',
              classification: 'trivial',
            },
          ],
        },
      });
    });
    await page.route('**/api/crash-recovery/pending/*/dismiss', async (route) => {
      dismissed.push(route.request().url());
      await route.fulfill({
        json: { status: 'dismissed', pending: null, commitSha: null, error: null },
      });
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');

    await expect(page.getByTestId('crash-recovery-prompt')).toBeHidden();
    const notification = page.getByTestId('notification-info')
      .filter({ hasText: 'Crash recovery found read-evidence sidecars' });
    await expect(notification).toBeVisible();
    await expect(notification).toContainText('4 metadata sidecar files remain uncommitted');
    await expect(notification).toContainText('Coding Agent Runner: 3 changed files');
    await expect(notification).toContainText('Coding Agent Chat: 1 changed file');

    const boardControl = page.getByRole('button', { name: /Add task/i }).first();
    await expect(boardControl).toBeEnabled();
    await boardControl.focus();
    await expect(boardControl).toBeFocused();

    const evidenceDir = process.env['CRASH_RECOVERY_RESULTS_DIR'];
    if (evidenceDir) {
      await mkdir(evidenceDir, { recursive: true });
      await setTheme(page, 'light');
      await page.screenshot({
        path: join(evidenceDir, 'crash-recovery-trivial-board-ready.png'),
        fullPage: false,
      });
      await setTheme(page, 'dark');
      await page.screenshot({
        path: join(evidenceDir, 'crash-recovery-trivial-board-ready-dark.png'),
        fullPage: false,
      });
    }

    await notification.getByTestId('crash-recovery-trivial-dismiss').click();
    await expect(notification).toBeHidden();
    await expect.poll(() => dismissed.length).toBe(2);
  });
});
