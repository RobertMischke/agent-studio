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
interface PipelineStepCondition { when: string; value?: string | null }
interface PipelineStepSetting { enabled?: boolean | null; mode?: string | null; cliType?: string | null; model?: string | null; thinkingLevel?: string | null; prompt?: string | null; condition?: PipelineStepCondition | null }
interface ProjectSettingsProjection { pipelineSteps?: Record<string, PipelineStepSetting> }

const STEP = 'aspect-code-quality';
const HAIKU = 'claude-haiku-4-5';
// The abort-review step: opt-in (default off) and the one step whose run
// condition the runtime evaluates, so it is the only row exposing the
// condition control.
const ABORT_STEP = 'post-abort-review';

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-step-config');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-step-config');
})();

let projectName = '';
let projectSlug = '';
let originalOverride: PipelineStepSetting | null = null;
let originalAbortOverride: PipelineStepSetting | null = null;

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function enc(name: string): string {
  return encodeURIComponent(name);
}

async function getOverride(stepId: string): Promise<PipelineStepSetting | undefined> {
  const all = await api<Record<string, ProjectSettingsProjection>>('/api/projects/settings');
  return all[projectName]?.pipelineSteps?.[stepId];
}

async function getStepOverride(): Promise<PipelineStepSetting | undefined> {
  return getOverride(STEP);
}

async function setStep(body: { stepId: string; enabled?: boolean | null; mode?: string | null; cliType?: string | null; model?: string | null; thinkingLevel?: string | null; prompt?: string | null; condition?: PipelineStepCondition | null }): Promise<void> {
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
  originalAbortOverride = (await getOverride(ABORT_STEP)) ?? null;
});

test.afterAll(async () => {
  if (!projectName) return;
  // Restore the steps to their pre-test state (clear if they had no override).
  await setStep({
    stepId: STEP,
    enabled: originalOverride?.enabled ?? null,
    cliType: originalOverride?.cliType ?? null,
    model: originalOverride?.model ?? null,
    thinkingLevel: originalOverride?.thinkingLevel ?? null,
    prompt: originalOverride?.prompt ?? null,
    mode: originalOverride?.mode ?? null,
  });
  await setStep({
    stepId: ABORT_STEP,
    enabled: originalAbortOverride?.enabled ?? null,
    cliType: originalAbortOverride?.cliType ?? null,
    model: originalAbortOverride?.model ?? null,
    thinkingLevel: originalAbortOverride?.thinkingLevel ?? null,
    prompt: originalAbortOverride?.prompt ?? null,
    mode: originalAbortOverride?.mode ?? null,
    condition: originalAbortOverride?.condition ?? null,
  });
});

test('pipeline: pipeline-step section renders and a per-step model change persists', async ({ page }) => {
  // Start from a clean override so the model select begins on Inherit.
  await setStep({ stepId: STEP, enabled: null, cliType: null, model: null, thinkingLevel: null, prompt: null, mode: null });

  // Nav-rebuild step 2 (T5b): the pipeline-steps section moved out of Project
  // Settings onto the Pipeline rail; identical rows/controls, new mount.
  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await expect(row).toBeVisible();

  const agentPicker = page.getByTestId(`pipeline-step-agent-${STEP}`);
  await expect(agentPicker).toBeVisible();
  await expect(agentPicker).toContainText('CLI default');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-settings-defaults.png'), fullPage: true });

  // Pin this aspect to Claude / Haiku through the shared CLI+model picker;
  // assert it lands in settings.
  await agentPicker.click();
  await page.getByTestId(`pipeline-step-agent-picker-${STEP}-cli-claude`).click();
  await page.getByTestId(`pipeline-step-agent-picker-${STEP}-model-${HAIKU}`).click();
  await page.getByTestId(`pipeline-step-agent-picker-${STEP}-done`).click();
  await expect.poll(async () => (await getStepOverride())?.model).toBe(HAIKU);
  await expect.poll(async () => (await getStepOverride())?.cliType).toBe('claude');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-step-model-haiku.png'), fullPage: true });
});

test('pipeline: disabling a step persists enabled=false and line-through styling', async ({ page }) => {
  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  const toggle = page.getByTestId(`pipeline-step-enabled-${STEP}`);
  await expect(toggle).toBeChecked();

  await toggle.uncheck();
  await expect.poll(async () => (await getStepOverride())?.enabled).toBe(false);

  // The row reflects the disabled state (line-through name, dimmed). Asserted
  // via the stable data-enabled marker rather than a styling-dependent class.
  const row = page.getByTestId(`pipeline-step-row-${STEP}`);
  await expect(row).toHaveAttribute('data-enabled', 'false');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-step-disabled.png'), fullPage: true });
});

test('pipeline: abort-review exposes a run-condition control that persists', async ({ page }) => {
  // Start from a clean abort-review override (opt-in step, default off).
  await setStep({ stepId: ABORT_STEP, enabled: null, model: null, mode: null, condition: null });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  // The abort-review row renders (appended to the catalogue) and starts off.
  const row = page.getByTestId(`pipeline-step-row-${ABORT_STEP}`);
  await expect(row).toBeVisible();
  const toggle = page.getByTestId(`pipeline-step-enabled-${ABORT_STEP}`);
  await expect(toggle).not.toBeChecked();

  // Enabling an opt-in step must persist enabled=true (not clear the override).
  await toggle.check();
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.enabled).toBe(true);

  // The condition select is now live and defaults to "always" (empty value).
  const condition = page.getByTestId(`pipeline-step-condition-${ABORT_STEP}`);
  await expect(condition).toBeVisible();
  await expect(condition).toHaveValue('');

  // A non-value condition persists immediately.
  await condition.selectOption('on-nonzero-exit');
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.condition?.when).toBe('on-nonzero-exit');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-condition-nonzero-exit.png'), fullPage: true });

  // A value-bearing condition reveals a value input; it persists token + value.
  await condition.selectOption('task-type');
  const value = page.getByTestId(`pipeline-step-condition-value-${ABORT_STEP}`);
  await expect(value).toBeVisible();
  await value.fill('bug');
  await value.blur();
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.condition?.when).toBe('task-type');
  await expect.poll(async () => (await getOverride(ABORT_STEP))?.condition?.value).toBe('bug');

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '05-condition-task-type.png'), fullPage: true });
});
