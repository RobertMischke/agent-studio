import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Per-project pre/post pipeline-step config (this task).
 *
 * The Project Settings panel grows a "Pipeline steps" section: one row per
 * configurable step (from /api/projects/pipeline-catalogue) joined with the
 * project's current overrides. Aspect steps expose a model picker + an
 * enable toggle; tool/orchestrator gate steps expose a mode select. Each
 * control PUTs /api/projects/{name}/pipeline-step and the row re-renders
 * from the authoritative response.
 *
 * This spec drives the model picker + enable toggle for an aspect step and
 * asserts each write round-trips to project-settings.json (read back via the
 * settings projection). It runs against the dedicated Playwright project so
 * the writes never disturb a real project, and restores the step it touched
 * in afterAll.
 */

interface WatchPath { name: string; path: string }
interface PipelineStepSetting { enabled?: boolean | null; mode?: string | null; model?: string | null }
interface ProjectSettingsProjection { pipelineSteps?: Record<string, PipelineStepSetting> }

const STEP = 'aspect-code-quality';
const HAIKU = 'claude-haiku-4-5';

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-step-config');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-step-config');
})();

let projectName = '';
let projectSlug = '';
let originalOverride: PipelineStepSetting | null = null;

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function enc(name: string): string {
  return encodeURIComponent(name);
}

async function getStepOverride(): Promise<PipelineStepSetting | undefined> {
  const all = await api<Record<string, ProjectSettingsProjection>>('/api/projects/settings');
  return all[projectName]?.pipelineSteps?.[STEP];
}

async function setStep(body: { stepId: string; enabled?: boolean | null; mode?: string | null; model?: string | null }): Promise<void> {
  await api(`/api/projects/${enc(projectName)}/pipeline-step`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);
  originalOverride = (await getStepOverride()) ?? null;
});

test.afterAll(async () => {
  if (!projectName) return;
  // Restore the step to its pre-test state (clear if it had no override).
  await setStep({
    stepId: STEP,
    enabled: originalOverride?.enabled ?? null,
    model: originalOverride?.model ?? null,
    mode: originalOverride?.mode ?? null,
  });
});

test('settings: pipeline-step section renders and a per-step model change persists', async ({ page }) => {
  // Start from a clean override so the model select begins on Inherit.
  await setStep({ stepId: STEP, enabled: null, model: null, mode: null });

  await page.goto(`/#/projects/${projectSlug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await expect(row).toBeVisible();

  const modelSelect = page.getByTestId(`pipeline-step-model-${STEP}`);
  await expect(modelSelect).toHaveValue(''); // Inherit

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-defaults.png'), fullPage: true });

  // Pin this aspect to Haiku through the UI; assert it lands in settings.
  await modelSelect.selectOption(HAIKU);
  await expect(modelSelect).toHaveValue(HAIKU);
  await expect.poll(async () => (await getStepOverride())?.model).toBe(HAIKU);

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-step-model-haiku.png'), fullPage: true });
});

test('settings: disabling a step persists enabled=false and line-through styling', async ({ page }) => {
  await page.goto(`/#/projects/${projectSlug}/settings`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  const toggle = page.getByTestId(`pipeline-step-enabled-${STEP}`);
  await expect(toggle).toBeChecked();

  await toggle.uncheck();
  await expect.poll(async () => (await getStepOverride())?.enabled).toBe(false);

  // The row gets the disabled modifier (line-through name, dimmed).
  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await expect(row).toHaveClass(/proj-detail__pl-row--disabled/);

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-step-disabled.png'), fullPage: true });
});
