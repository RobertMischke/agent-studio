import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * F35 — per-lane sort strategy.
 *
 * Covers the three user-visible surfaces:
 *  1. Project Settings: a "Sort order per lane" section with one dropdown
 *     per lane, pre-selected to the resolved strategy, that PUTs on change.
 *  2. Board lane-header: a sort-strategy indicator glyph per lane whose
 *     data-strategy reflects the resolved strategy.
 *  3. Drag gating: only `manual` lanes expose drop-zones; auto-sorted lanes
 *     drop them so the card order cannot be hand-reordered.
 *
 * The spec runs against the dedicated "Playwright Test" project so the
 * setting writes never disturb a real project, and restores the two lanes
 * it touches in afterAll. No jobs are created — the indicator falls back to
 * the active-project filter for empty lanes, so the board assertions hold
 * regardless of how many cards the project has.
 */

interface WatchPath { name: string; path: string }
interface LaneSortResponse {
  resolved: Record<string, string>;
  overrides: Record<string, string>;
  available: string[];
}

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'lane-sort-strategy');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'lane-sort-strategy');
})();

const MANUAL_LANE = '0-backlog';
const AUTO_LANE = '1-preparation';

let projectName = '';
let projectSlug = '';
let originalManualOverride: string | null = null;
let originalAutoOverride: string | null = null;

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function enc(name: string): string {
  return encodeURIComponent(name);
}

async function getLaneSort(): Promise<LaneSortResponse> {
  return api<LaneSortResponse>(`/api/projects/${enc(projectName)}/lane-sort-strategies`);
}

async function setLaneSort(lane: string, strategy: string): Promise<void> {
  await api(`/api/projects/${enc(projectName)}/lane-sort-strategy`, {
    method: 'PUT',
    body: JSON.stringify({ lane, strategy }),
  });
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);

  const before = await getLaneSort();
  originalManualOverride = before.overrides[MANUAL_LANE] ?? null;
  originalAutoOverride = before.overrides[AUTO_LANE] ?? null;
});

test.afterAll(async () => {
  // Restore the two lanes we touched to their pre-test override (or clear).
  if (!projectName) return;
  await setLaneSort(MANUAL_LANE, originalManualOverride ?? '');
  await setLaneSort(AUTO_LANE, originalAutoOverride ?? '');
});

test('workflow: per-lane dropdowns render resolved strategy and persist a change', async ({ page }) => {
  // Nav-rebuild step 2 (T5b): the per-lane sort section moved out of Project
  // Settings onto the Workflow rail; same dropdowns, same PUT, new mount.
  await page.goto(`/#/projects/${projectSlug}/workflow`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-lane-sort');
  await expect(section).toBeVisible();

  // Every dropdown is pre-selected to the backend-resolved strategy.
  const resolved = (await getLaneSort()).resolved;
  for (const [lane, strategy] of Object.entries(resolved)) {
    const select = page.getByTestId(`lane-sort-select-${lane}`);
    if (await select.count() === 0) continue; // internal lanes are not surfaced
    await expect(select).toHaveValue(strategy);
  }

  // The settings panel scrolls inside its own container, so bring the
  // sort-strategy section into view before capturing — otherwise a fullPage
  // shot only sees the top of the panel and the new dropdowns are clipped.
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeInViewport();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-defaults.png'), fullPage: true });

  // Change the Backlog lane to Manual through the UI and confirm the write
  // round-trips to the backend override map.
  const backlogSelect = page.getByTestId(`lane-sort-select-${MANUAL_LANE}`);
  await backlogSelect.selectOption('manual');
  await expect(backlogSelect).toHaveValue('manual');

  await expect.poll(async () => (await getLaneSort()).overrides[MANUAL_LANE]).toBe('manual');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-settings-backlog-manual.png'), fullPage: true });
});

test('board: lane-header indicator reflects strategy and drag is gated on manual', async ({ page }) => {
  // Deterministic fixture: Backlog = manual (drag on), Preparation =
  // newest-first (drag off). Set via API so the board only has to render.
  await setLaneSort(MANUAL_LANE, 'manual');
  await setLaneSort(AUTO_LANE, 'newest-first');

  // Scope the board to this project by seeding the studio-shell tab state so
  // the active editor tab is a board pinned to `projectName`. The shell's
  // active-tab effect then runs setSoleProject(projectName) — the only path
  // that makes activeProjects stick under the default VS-Code layout. A bare
  // `activeProjects` localStorage write is overwritten by that effect on every
  // load (the "All projects" tab clears the filter on mount), and the legacy
  // project-filter chip is not rendered in the studio layout. With only this
  // project active, the empty Backlog/Preparation lanes fall back to its
  // resolved strategy for the indicator + drag gate.
  await page.goto('/');
  await page.evaluate((name) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__', sticky: true },
        { kind: 'board', projectName: name },
      ],
      activeKey: `board:${name}`,
    }));
    localStorage.setItem('activeProjects', JSON.stringify([name]));
    localStorage.removeItem('collapsedLanes');
    location.hash = '';
  }, projectName);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');

  // The indicator only appears once the per-project strategy store has
  // loaded (slow poll, primed on board mount).
  const manualIndicator = page.getByTestId(`lane-sort-indicator-${MANUAL_LANE}`);
  await expect(manualIndicator).toBeVisible({ timeout: 15_000 });
  await expect(manualIndicator).toHaveAttribute('data-strategy', 'manual');

  const autoIndicator = page.getByTestId(`lane-sort-indicator-${AUTO_LANE}`);
  await expect(autoIndicator).toBeVisible();
  await expect(autoIndicator).toHaveAttribute('data-strategy', 'newest-first');

  // Drag gating: the manual lane keeps its drop-zones; the auto-sorted lane
  // drops them entirely (the trailing drop-zone renders only when reorder
  // is enabled, so its absence proves drag is disabled even on an empty lane).
  const manualDropZones = page.locator(`[data-testid="lane-${MANUAL_LANE}"] .column__drop-zone`);
  const autoDropZones = page.locator(`[data-testid="lane-${AUTO_LANE}"] .column__drop-zone`);
  await expect(manualDropZones.first()).toBeAttached();
  await expect(autoDropZones).toHaveCount(0);

  await manualIndicator.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-board-indicators.png'), fullPage: true });
});
