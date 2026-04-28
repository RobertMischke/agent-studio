import { test, expect } from '@playwright/test';
import { api } from './helpers/api';

/**
 * Per-project Auto-pickup toggle in the header chip strip.
 *
 * The toggle drives the runner mode:
 *   off       -> "manual" / "paused" without an active job
 *   on        -> "auto-continuous" — next Ready task starts when current finishes
 *   stopping  -> "paused" while a task is still running (current finishes, no more pickup)
 *
 * The button label, color, count badge, and tooltip must reflect that state.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

test.describe('Auto-pickup toggle', () => {
  let projectName = '';

  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    projectName = paths[0].name;
  });

  test.afterEach(async () => {
    // Restore default mode so subsequent specs / live runs aren't affected.
    if (projectName) {
      await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
        method: 'PUT',
        body: JSON.stringify({ mode: 'manual' })
      });
    }
  });

  test('toggle button is rendered next to each project chip with a tooltip', async ({ page }) => {
    await page.goto('/');

    const toggle = page.getByTestId(`auto-toggle-${projectName}`);
    await expect(toggle).toBeVisible();

    const tip = await toggle.getAttribute('title');
    expect(tip).toBeTruthy();
    expect(tip!.toLowerCase()).toContain('auto');
  });

  test('clicking the toggle enables auto-continuous and updates the visual state', async ({ page }) => {
    // Make sure we start from off
    await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'manual' })
    });

    await page.goto('/');

    const toggle = page.getByTestId(`auto-toggle-${projectName}`);
    await expect(toggle).toBeVisible();
    await expect(toggle).not.toHaveClass(/auto-toggle--on/);

    await toggle.click();

    // The runner status is polled — wait for the visual to flip on.
    await expect(toggle).toHaveClass(/auto-toggle--on/, { timeout: 5000 });

    // Backend should reflect the same mode.
    const status = await api<{ projects: Record<string, { mode: string }> }>('/api/runner/status');
    expect(status.projects[projectName].mode).toBe('auto-continuous');

    // Tooltip now talks about stopping rather than enabling.
    const tip = await toggle.getAttribute('title');
    expect(tip!.toLowerCase()).toContain('click to stop');
  });

  test('count badge reflects the number of queued Ready tasks for the project', async ({ page }) => {
    const status = await api<{ projects: Record<string, { queuedJobIds: string[] }> }>('/api/runner/status');
    const queued = status.projects[projectName]?.queuedJobIds.length ?? 0;
    test.skip(queued === 0, 'no Ready tasks queued for this project');

    await page.goto('/');

    const count = page.getByTestId(`auto-count-${projectName}`);
    await expect(count).toBeVisible();
    await expect(count).toHaveText(String(queued));
  });
});
