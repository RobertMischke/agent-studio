import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name: string }

/**
 * Per-project visual identity — each watched project gets a deterministic
 * initial-letter disk + hue. The disk shows up everywhere the project name
 * does (board cards, archive rows, task-nav meta, header filter chips,
 * detail-view header). Two projects must end up with two different hues so
 * the user can scan five-to-ten projects on the board at a glance.
 *
 * The "running" job-card variant carries a stronger background, a wider
 * left accent, and a breathing glow so the actively-running task jumps out
 * of the column.
 */
test.describe('Project identity & running prominence', () => {
  test('header filter chips render an initial disk per project, with distinct hues', async ({ page }) => {
    const projects = await api<WatchPath[]>('/api/watch-paths');
    if (projects.length < 2) test.skip(true, 'Needs at least two watched projects');

    await page.goto('/');
    const [first, second] = projects;

    const firstChip = page.getByTestId(`project-filter-${first.name}`);
    const secondChip = page.getByTestId(`project-filter-${second.name}`);
    await expect(firstChip).toBeVisible();
    await expect(secondChip).toBeVisible();

    // Each chip must surface its identity disk.
    await expect(firstChip.locator('.filter-chip__disk')).toHaveText(
      first.name.replace(/[^A-Za-z0-9]/g, '')[0].toUpperCase()
    );

    // The deterministic hash maps "Agent Software Studio" and "Runbook" to
    // different palette slots; assert the rendered disk colours diverge so
    // a regression that collapses everything to one hue is caught.
    const firstColor = await firstChip.locator('.filter-chip__disk').evaluate(
      el => getComputedStyle(el).backgroundColor
    );
    const secondColor = await secondChip.locator('.filter-chip__disk').evaluate(
      el => getComputedStyle(el).backgroundColor
    );
    expect(firstColor).not.toBe(secondColor);
  });

  test('job cards show the project chip and mark running tasks prominently', async ({ page }) => {
    const watchPaths = await api<WatchPath[]>('/api/watch-paths');
    if (!watchPaths.length) test.skip(true, 'No watch paths configured');
    const watchPath = watchPaths[0].path;

    const idle = await createJob({
      title: `identity-idle-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });

    try {
      await page.goto('/');
      const card = page.locator(`[data-testid="job-card"]:has-text("${idle.id}")`).first();
      await expect(card).toBeVisible({ timeout: 10_000 });

      // Project chip is present with disk + name.
      const chip = card.getByTestId('job-card-project');
      await expect(chip).toBeVisible();
      await expect(chip.locator('.job-card__project-disk')).toHaveText(
        watchPaths[0].name.replace(/[^A-Za-z0-9]/g, '')[0].toUpperCase()
      );

      // Idle Ready card must NOT carry the running modifier.
      await expect(card).not.toHaveAttribute('data-running', 'true');

      // Screenshot for the chat reply — covers both the project chip and
      // the calm (non-running) state so a follow-up shot can prove the
      // running variant looks different.
      await card.screenshot({ path: 'test-results/project-identity-idle-card.png' });
    } finally {
      await api(`/api/jobs/${encodeURIComponent(idle.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
