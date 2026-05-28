import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import type { Page } from '@playwright/test';
import path from 'node:path';

/**
 * Visual fixture: capture the In-Progress lane header with the new
 * status cluster in each interesting state so the operator can compare
 * against the old single-pill design.
 *
 * Produces six PNGs under `screenshots/lane-status-cluster/`:
 *   - dark + light × { AUTO, MANUAL, PAUSED }
 *
 * Pure read-only on the UI side. Mutates runner mode through the API
 * and restores manual on teardown so we never strand the system.
 */

interface WatchPath { name: string; path: string; rootPath: string; }
interface RunnerStatusResponse {
  projects: Record<string, { activeJobId: string | null; mode: string }>;
}

const OUT = 'screenshots/lane-status-cluster';

async function setMode(name: string, mode: string) {
  await api(`/api/runner/${encodeURIComponent(name)}/mode`, {
    method: 'PUT',
    body: JSON.stringify({ mode })
  });
}

async function openBoard(page: Page, name: string) {
  // Wider viewport so the In-Progress lane has room for the three-pill
  // cluster without truncating the trailing chips. The reel is a fixture
  // for documenting the visual, not for layout testing.
  await page.setViewportSize({ width: 1920, height: 1000 });
  await page.goto('/');
  const welcome = page.getByRole('button', { name: new RegExp(`^${name}\\s+\\d+$`) }).first();
  try { await welcome.click({ timeout: 4000 }); } catch { /* already in a tab */ }
  await page.getByTestId('lane-3-progress').first().waitFor({ state: 'visible', timeout: 8000 });
}

async function setTheme(page: Page, theme: 'dark' | 'light') {
  await page.evaluate((t) => { document.documentElement.dataset.studioTheme = t; }, theme);
  await page.waitForTimeout(150);
}

async function shoot(page: Page, name: string) {
  // Snap the whole lane so the cluster sits in context: lane title,
  // count badge, and the trailing pills. Cropping to the header alone
  // truncated the chips when the lane width fell below ~340px.
  const lane = page.getByTestId('lane-3-progress').first();
  await lane.screenshot({ path: path.join(OUT, name) });
}

test.describe('Lane status cluster — visual reel', () => {
  let project = '';

  test.beforeAll(async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const status = await api<RunnerStatusResponse>('/api/runner/status');
    const withActive = paths.find(p => status.projects[p.name]?.activeJobId);
    project = (withActive ?? paths[0]).name;
  });

  test.afterAll(async () => {
    if (project) await setMode(project, 'manual');
  });

  for (const theme of ['dark', 'light'] as const) {
    test(`${theme} theme — AUTO / MANUAL / PAUSED`, async ({ page }) => {
      await openBoard(page, project);
      await setTheme(page, theme);

      const pill = page.getByTestId('lane-auto-toggle-3-progress').first();
      const cases: Array<[mode: string, kind: 'auto' | 'manual' | 'paused', file: string]> = [
        ['auto-continuous', 'auto',   `${theme}-auto.png`],
        ['manual',          'manual', `${theme}-manual.png`],
        ['paused',          'paused', `${theme}-paused.png`],
      ];
      for (const [mode, kind, file] of cases) {
        await setMode(project, mode);
        // Wait for the runner-status poll to land. Snapping right after
        // the PUT would race the next /api/runner/status response and
        // capture the previous chip — easy to miss in dev, exactly the
        // class of staleness the cluster fix is about.
        await expect(pill).toHaveAttribute('data-mode-kind', kind, { timeout: 6000 });
        await shoot(page, file);
      }
    });
  }
});
