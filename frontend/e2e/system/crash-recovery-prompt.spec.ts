import { expect, test } from '@playwright/test';

test.describe('Crash recovery prompt', () => {
  test('shows pending startup recovery items and commits only after confirmation', async ({ page }) => {
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
});
