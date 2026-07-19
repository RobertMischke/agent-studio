import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Lane status cluster on the In-Progress (`3-progress`) lane header.
 *
 * The bug this spec locks down: the lane used to show a single "MANUAL"
 * pill even while a task was actively running. The cluster replaces it
 * with three non-contradicting signals:
 *
 *   - RUNNING pill: when the runner has an active execution (live dot,
 *     job id + duration + model).
 *   - mode pill: AUTO / MANUAL / PAUSED — what happens NEXT once the
 *     current task finishes. Visually distinct so MANUAL (user toggle)
 *     and PAUSED (circuit-breaker / supervisor) are not confused.
 *   - Q:N pill: queued count from the `2-ready` lane.
 *
 * The three scenarios from the acceptance criteria:
 *   1. idle + auto-continuous → AUTO chip, no RUNNING pill.
 *   2. running + auto-continuous → BOTH RUNNING and AUTO chips.
 *   3. paused (mode='paused' or manual+circuit-breaker) → PAUSED chip
 *      with a tooltip that names the cause.
 *
 * The circuit-breaker → PAUSED rendering is exercised in the unit spec
 * (`task-column.spec.ts`) because forcing 3x consecutive auto-failures
 * end-to-end is prohibitively expensive in CI.
 */

import type { Page } from '@playwright/test';

interface WatchPath { name: string; path: string; rootPath: string; }
interface RunnerStatusResponse {
  projects: Record<string, {
    mode: string;
    activeJobId: string | null;
    queuedJobIds: string[];
    modeReason?: string | null;
    modeSource?: string | null;
  }>;
}

/**
 * Open the named project's board so the In-Progress lane renders and
 * `laneAutoProject(...)` resolves to the scoped project. Without this the
 * studio shell parks on its Welcome view and no lane chip is in the DOM.
 */
async function openBoardForProject(page: Page, name: string) {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  // Welcome view exposes a project button per project; click ours so a
  // board tab opens. Falls back to the explorer-pane button if the
  // welcome layout has already been dismissed by a prior test run.
  const welcomeButton = page.getByRole('button', { name: new RegExp(`^${name}\\s+\\d+$`) }).first();
  try {
    await welcomeButton.click({ timeout: 4000 });
  } catch {
    // Already on a tab — make sure the active tab targets this project
    // via the breadcrumb picker.
    const picker = page.getByRole('button', { name: /^All projects/ }).or(
      page.getByRole('button', { name: new RegExp(name) })
    ).first();
    if (await picker.isVisible().catch(() => false)) {
      await picker.click();
      const opt = page.getByRole('button', { name: new RegExp(`^${name}\\b`) }).first();
      if (await opt.isVisible().catch(() => false)) await opt.click();
    }
  }
  // Wait until at least one lane renders so subsequent assertions don't
  // race with the first board paint.
  await page.getByTestId('lane-3-progress').first().waitFor({ state: 'visible', timeout: 8000 });
}

/**
 * Hover the target locator and return the text of the singleton
 * `<div data-testid="cac-tooltip">` the `[appTooltip]` directive injects
 * via `TooltipController`. The directive renders into `document.body`
 * (not as a `title` attribute), so plain `getAttribute('title')` returns
 * empty; this helper exists so specs that exercise the tooltip don't
 * each re-roll the hover dance.
 */
async function readTooltipForLocator(page: Page, locator: import('@playwright/test').Locator): Promise<string> {
  await locator.hover();
  const root = page.getByTestId('cac-tooltip').first();
  await root.waitFor({ state: 'attached', timeout: 4000 });
  return ((await root.textContent()) ?? '').trim();
}

test.describe('Lane status cluster — In-Progress lane', () => {
  let projectName = '';

  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    // Use the project that currently has an active job if any so the
    // RUNNING-pill scenario lights up without us having to start one.
    const status = await api<RunnerStatusResponse>('/api/runner/status');
    const withActive = paths.find(p => status.projects[p.name]?.activeJobId);
    projectName = (withActive ?? paths[0]).name;
  });

  test.afterEach(async () => {
    // Restore manual so we never strand the system in auto/paused on the
    // way out of a test that mutated the mode.
    if (projectName) {
      await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
        method: 'PUT',
        body: JSON.stringify({ mode: 'manual' })
      });
    }
  });

  test('mode pill renders MANUAL with the explanatory tooltip', async ({ page }) => {
    await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'manual' })
    });
    await openBoardForProject(page, projectName);
    const pill = page.getByTestId('lane-auto-toggle-3-progress').first();
    await expect(pill).toBeVisible({ timeout: 8000 });
    await expect(pill).toHaveAttribute('data-mode-kind', 'manual', { timeout: 6000 });
    await expect(pill).toContainText('MANUAL');
    const tip = await readTooltipForLocator(page, pill);
    expect(tip.toLowerCase()).toContain('auto-pickup is off');
  });

  test('mode pill renders AUTO when the runner is auto-continuous', async ({ page }) => {
    await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'auto-continuous' })
    });
    await openBoardForProject(page, projectName);
    const pill = page.getByTestId('lane-auto-toggle-3-progress').first();
    await expect(pill).toBeVisible({ timeout: 8000 });
    await expect(pill).toHaveAttribute('data-mode-kind', 'auto', { timeout: 5000 });
    await expect(pill).toContainText('AUTO');
  });

  test('mode pill renders PAUSED when runner mode is paused', async ({ page }) => {
    await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'paused' })
    });
    await openBoardForProject(page, projectName);
    const pill = page.getByTestId('lane-auto-toggle-3-progress').first();
    await expect(pill).toBeVisible({ timeout: 8000 });
    await expect(pill).toHaveAttribute('data-mode-kind', 'paused', { timeout: 5000 });
    await expect(pill).toContainText('PAUSED');
    // The paused tooltip explains what triggered the halt so the operator
    // is not left guessing whether the system or they paused it.
    const tip = await readTooltipForLocator(page, pill);
    expect(tip.toLowerCase()).toContain('paused');
  });

  test('RUNNING pill shows when an active job is present', async ({ page }) => {
    const status = await api<RunnerStatusResponse>('/api/runner/status');
    const active = status.projects[projectName]?.activeJobId;
    test.skip(!active, 'no active job in 3-progress; RUNNING pill scenario skipped');

    await api(`/api/runner/${encodeURIComponent(projectName)}/mode`, {
      method: 'PUT',
      body: JSON.stringify({ mode: 'auto-continuous' })
    });
    await openBoardForProject(page, projectName);

    const running = page.getByTestId('lane-running-pill-3-progress').first();
    await expect(running).toBeVisible({ timeout: 8000 });
    await expect(running).toHaveAttribute('data-job-id', active!);
    await expect(running).toContainText('RUNNING');

    // Mode pill remains AUTO alongside RUNNING — this is the bug fix:
    // the two signals must not contradict each other.
    const mode = page.getByTestId('lane-auto-toggle-3-progress').first();
    await expect(mode).toHaveAttribute('data-mode-kind', 'auto');
  });
});
