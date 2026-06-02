import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * DRIFT Nachtrag: the five drift dimensions surface in the per-project
 * pipeline-step config as opt-in post-steps that default OFF. The live dev
 * backend may still be running an older build whose catalogue predates the
 * drift steps, so this spec routes /api/projects/pipeline-catalogue to the
 * real response augmented with the five drift steps (defaultEnabled=false) -
 * exactly the shape the updated ProjectSettingsEndpoints now emits. It then
 * asserts the drift rows render disabled by default (no override -> fall back
 * to defaultEnabled=false) while a normal aspect step stays enabled, and
 * captures a screenshot.
 */

interface WatchPath { name: string; path: string }

const DRIFT_STEPS = [
  { id: 'post-drift-adr-code', displayName: 'Drift: ADR / Code' },
  { id: 'post-drift-software-architecture', displayName: 'Drift: Software / Architecture' },
  { id: 'post-drift-docs-marketing', displayName: 'Drift: Docs / Marketing' },
  { id: 'post-drift-spec-task-job', displayName: 'Drift: Spec / Task / Job' },
  { id: 'post-drift-code-pattern', displayName: 'Drift: Code-Pattern (rule-based)' },
];

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-drift-steps');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-drift-steps');
})();

let projectSlug = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectSlug = slugFor(preferred.name);
});

test('settings: drift dimensions appear as opt-in post-steps that default OFF', async ({ page }) => {
  // Augment the catalogue with the drift steps the new backend emits. Existing
  // steps get defaultEnabled=true so they keep rendering enabled; drift steps
  // get defaultEnabled=false (opt-in).
  await page.route('**/api/projects/pipeline-catalogue', async route => {
    const res = await route.fetch();
    const body = await res.json();
    const steps = (body.steps ?? []).map((s: Record<string, unknown>) => ({
      ...s,
      defaultEnabled: s.defaultEnabled ?? true,
    }));
    for (const d of DRIFT_STEPS) {
      steps.push({
        id: d.id,
        displayName: d.displayName,
        kind: 'drift',
        usesModel: true,
        supportsMode: false,
        canDisable: true,
        defaultEnabled: false,
      });
    }
    await route.fulfill({
      response: res,
      json: { ...body, steps },
    });
  });

  await page.goto(`/#/projects/${projectSlug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  // Every drift row renders, and every one is disabled by default.
  for (const d of DRIFT_STEPS) {
    const row = page.getByTestId(`pipeline-step-row-${d.id}`);
    await expect(row).toBeVisible();
    await expect(row).toHaveClass(/proj-detail__pl-row--disabled/);
  }

  // A normal aspect step is NOT disabled by default - proves the default-OFF
  // behaviour is specific to drift, not a blanket "everything off".
  const aspectRow = page.getByTestId('pipeline-step-row-aspect-code-quality');
  await expect(aspectRow).toBeVisible();
  await expect(aspectRow).not.toHaveClass(/proj-detail__pl-row--disabled/);

  const driftRow = page.getByTestId('pipeline-step-row-post-drift-adr-code');
  await driftRow.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-drift-steps-default-off.png'), fullPage: true });
});
