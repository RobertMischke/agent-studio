import { test } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Snapshots of the per-project Auto-pickup pill in its three visible states:
 * off (manual), on (auto-continuous), and stopping (paused while a task runs).
 *
 * Pure visual fixture — used to attach inline screenshots in PR / chat reports.
 */

test.describe('Auto-pickup toggle — visual states', () => {
  test('off / on / stopping screenshots', async ({ page }) => {
    const paths = await api<{ name: string }[]>('/api/watch-paths');
    // Pick the project with an active job if any, so we can capture the
    // "stopping" state (paused while a task is still running).
    const status = await api<{ projects: Record<string, { activeJobId: string | null }> }>('/api/runner/status');
    const withActive = paths.find(p => status.projects[p.name]?.activeJobId);
    const name = (withActive ?? paths[0]).name;

    // OFF — make sure the runner is in manual mode
    await api(`/api/runner/${encodeURIComponent(name)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'manual' })
    });
    await page.goto('/');
    let chip = page.getByTestId(`auto-toggle-${name}`);
    await chip.waitFor({ state: 'visible' });
    await page.locator('header.header').screenshot({
      path: 'test-results/auto-pickup-off.png'
    });

    // ON — switch to auto-continuous
    await chip.click();
    await page.waitForTimeout(400);
    await page.locator('header.header').screenshot({
      path: 'test-results/auto-pickup-on.png'
    });

    // STOPPING — set paused while a task is running.
    // We can only see this state if the project actually has an active job.
    const liveStatus = await api<{ projects: Record<string, { activeJobId: string | null }> }>('/api/runner/status');
    if (liveStatus.projects[name]?.activeJobId) {
      await api(`/api/runner/${encodeURIComponent(name)}/mode`, {
        method: 'PUT',
        body: JSON.stringify({ mode: 'paused' })
      });
      await page.waitForTimeout(2500); // poll interval
      await page.locator('header.header').screenshot({
        path: 'test-results/auto-pickup-stopping.png'
      });
    }

    // Restore manual so we leave the system in a known state
    await api(`/api/runner/${encodeURIComponent(name)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'manual' })
    });
  });
});
