import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * T4a evidence — the reworked project-level Pipeline page.
 *
 * Renders the real compiled <app-project-pipeline-panel> in a browser with a
 * deterministic mocked catalogue / overrides / cost so the screenshot shows
 * every control the page now owns: the pre / core / post grouping, per-step
 * activation + ordering, the per-step model picker, the prompt *binding*
 * reference to the Prompts registry (content lives there, never inline here)
 * plus the legacy inline-override escape hatch, the gate / run-condition
 * controls, and the cost-by-step-kind rollup. Pure reads + route mocks, so it
 * is safe against the shared stack; the captured shots are labelled --mocked
 * because the panel data is mocked (the component + SCSS are the real build).
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-page');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-page');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

const CATALOGUE = {
  pipelineId: 'default',
  steps: [
    { id: 'pre-context-scan', displayName: 'Pre: Context scan', kind: 'tool', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'pre-context-scan', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'core-run', displayName: 'Core: Agent run', kind: 'core', usesModel: false, usesPrompt: false, supportsMode: false, canDisable: false, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-requirement-fit', displayName: 'Aspect: Requirement fit', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-requirement-fit', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-code-quality', displayName: 'Aspect: Code quality', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-code-quality', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-security', displayName: 'Aspect: Security', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-security', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'decision-gate', displayName: 'Decision: Lint gate', kind: 'tool', usesModel: false, usesPrompt: false, supportsMode: true, canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'post-abort-review', displayName: 'Post: Abort review', kind: 'tool', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'post-abort-review', canDisable: true, defaultEnabled: false, supportsCondition: true },
  ],
};

const SETTINGS_PROJECTION = {
  pipelineSteps: {
    'aspect-code-quality': { enabled: true, cliType: 'claude', model: 'claude-haiku-4-5', thinkingLevel: null },
    // A legacy inline prompt override -> renders the "inline override" badge + Clear.
    'aspect-security': { enabled: true, prompt: 'Project-specific security checklist (legacy inline).' },
    // Opt-in step turned on with a run condition.
    'post-abort-review': { enabled: true, condition: { when: 'on-nonzero-exit' } },
  },
  pipelineStepOrder: [],
};

function fakeCost(project: string) {
  const k = (kind: string, tokens: number, cost: number, unknown = false) =>
    ({ kind, totalTokens: tokens, totalCostUsd: cost, anyModelUnknown: unknown, cells: [] });
  const kinds = [k('core', 480000, 1.44), k('aspect', 96000, 0.31), k('tool', 21000, 0.011, true)];
  return {
    project, days: [], windowDays: 30, kinds,
    totalTokens: 597000, totalCostUsd: 1.761, anyModelUnknown: true,
    taskCount: 7, hasData: true, fetchedAt: '2026-06-10T00:00:00Z',
  };
}

let projectSlug = '';
let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);
});

test('pipeline page: reworked panel shows steps, models, prompt bindings, cost', async ({ page }) => {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/projects/pipeline-catalogue**', r => r.fulfill(json(CATALOGUE)));
  await page.route('**/api/projects/settings', r => r.fulfill(json({ [projectName]: SETTINGS_PROJECTION })));
  await page.route('**/token-usage/pipeline-cost*', r => r.fulfill(json(fakeCost(projectName))));

  // Tall viewport so the shell's inner scroll area shows the whole panel
  // (every phase group + the cost rollup) in one shot.
  await page.setViewportSize({ width: 1440, height: 2400 });

  await page.goto(`/#/projects/${projectSlug}/pipeline`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();

  // Phase groups: core renders "always on"; aspects expose a model picker; a
  // prompt cell deep-links to the Prompts registry rather than editing inline.
  await expect(page.getByTestId('pipeline-step-row-core-run')).toBeVisible();
  const codeQualityRow = page.getByTestId('pipeline-step-row-aspect-code-quality');
  await codeQualityRow.evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(codeQualityRow.getByTestId('pipeline-step-setting-run-aspect-code-quality')).toBeVisible();
  await expect(codeQualityRow.getByTestId('pipeline-step-setting-run-aspect-code-quality')).toContainText('sequential');
  await expect(codeQualityRow.getByTestId('pipeline-step-setting-model-aspect-code-quality')).toBeVisible();
  await expect(page.getByTestId('pipeline-step-prompt-open-aspect-code-quality')).toBeVisible();
  await expect(page.getByTestId('pipeline-step-agent-aspect-code-quality')).toBeVisible();
  await page.getByTestId('pipeline-step-row-aspect-requirement-fit').evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-prompt-manage-aspect-requirement-fit')).toBeVisible();
  // The inline-override step exposes its clear-to-registry escape hatch.
  await page.getByTestId('pipeline-step-row-aspect-security').evaluate(el => { (el as HTMLDetailsElement).open = true; });
  await expect(page.getByTestId('pipeline-step-prompt-clear-aspect-security')).toBeVisible();
  // Cost-by-step-kind rollup from the mocked window.
  await expect(page.getByTestId('pipeline-cost-legend-core')).toBeVisible();
  await expect(page.getByTestId('pipeline-cost-total')).toBeVisible();

  await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-full--mocked.png'), fullPage: true });
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-section--mocked.png') });
});
