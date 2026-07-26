import { test, expect } from '../fixtures/dev-backend';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';
import type { Page } from '@playwright/test';

/**
 * T4a evidence — the reworked project-level Pipeline page.
 *
 * Renders the real compiled <app-project-pipeline-panel> in a browser with a
 * deterministic mocked catalogue / overrides / cost so the screenshot shows
 * every control the page now owns: the pre / core / post grouping, per-step
 * activation + ordering, the per-step model picker, the prompt *binding*
 * reference to the Prompts registry (content lives there, never inline here)
 * plus the legacy inline-override escape hatch, the gate / run-condition
 * controls, and each step's 90-day token sum. Pure reads + route mocks, so it
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
    { id: 'pre-context-scan', displayName: 'Pre: Context scan', kind: 'module', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'pre-context-scan', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'core-run', displayName: 'Core: Agent run', kind: 'core', usesModel: false, usesPrompt: false, supportsMode: false, canDisable: false, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-requirement-fit', displayName: 'Aspect: Requirement fit', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-requirement-fit', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-code-quality', displayName: 'Aspect: Code quality', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-code-quality', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'aspect-security', displayName: 'Aspect: Security', kind: 'aspect', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'aspect-security', canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'decision-gate', displayName: 'Decision: Lint gate', kind: 'tool', usesModel: false, usesPrompt: false, supportsMode: true, canDisable: true, defaultEnabled: true, supportsCondition: false },
    { id: 'post-abort-review', displayName: 'Post: Abort review', kind: 'orchestrator', usesModel: true, usesPrompt: true, supportsMode: false, promptTemplate: 'post-abort-review', canDisable: true, defaultEnabled: true, supportsCondition: true },
  ],
};

const SETTINGS_PROJECTION = {
  pipelineSteps: {
    'aspect-code-quality': { enabled: true, cliType: 'claude', model: 'claude-haiku-4-5', thinkingLevel: null },
    // A legacy inline prompt override -> renders the "inline override" badge + Clear.
    'aspect-security': { enabled: true, prompt: 'Project-specific security checklist (legacy inline).' },
    // Opt-out step (default on) with a run condition set.
    'post-abort-review': { enabled: true, condition: { when: 'on-nonzero-exit' } },
  },
  pipelineStepOrder: [],
};

function fakeCost(project: string) {
  const k = (kind: string, tokens: number, cost: number, unknown = false) =>
    ({ kind, totalTokens: tokens, totalCostUsd: cost, anyModelUnknown: unknown, cells: [] });
  const kinds = [k('core', 480000, 1.44), k('aspect', 96000, 0.31), k('module', 21000, 0.011, true)];
  const s = (stepId: string, kind: string, tokens: number, cost: number, unknown = false) =>
    ({ stepId, kind, totalTokens: tokens, totalCostUsd: cost, anyModelUnknown: unknown });
  const steps = [
    s('core-run', 'core', 480000, 1.44),
    s('aspect-code-quality', 'aspect', 64000, 0.21),
    s('aspect-requirement-fit', 'aspect', 32000, 0.10),
    s('pre-context-scan', 'module', 21000, 0.011, true),
  ];
  return {
    project, days: [], windowDays: 90, kinds, steps,
    totalTokens: 597000, totalCostUsd: 1.761, anyModelUnknown: true,
    taskCount: 7, hasData: true, fetchedAt: '2026-06-10T00:00:00Z',
  };
}

let projectSlug = '';
let projectName = '';

async function proxyBackend(page: Page, baseUrl: string): Promise<void> {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/crash-recovery/pending') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      });
      return;
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname)) {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ models: [], source: 'pipeline-evidence' }),
      });
      return;
    }
    if (url.pathname === '/api/cli/quota' || url.pathname === '/api/cli/usage') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(url.pathname.endsWith('/quota')
          ? { at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }
          : { at: new Date().toISOString(), sessions: [] }),
      });
      return;
    }
    const response = await route.fetch({
      url: `${baseUrl}${url.pathname}${url.search}`,
      timeout: 30_000,
    });
    await route.fulfill({ response });
  });
}

test.beforeEach(async ({ page, devBackend }) => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test('pipeline page: reworked panel shows health, steps, models, prompt bindings, per-step tokens', async ({ page, devBackend }) => {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  const paths = await pathsResponse.json() as WatchPath[];
  const preferred = paths.find(p => /playwright|worktree/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);

  await page.route('**/api/**', r => r.fulfill(json({})));
  await page.route('**/api/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/clients/', r => r.fulfill(json([])));
  await page.route('**/api/tags', r => r.fulfill(json([])));
  await page.route('**/api/orchestrator/sessions', r => r.fulfill(json({ sessions: [] })));
  await page.route('**/api/crash-recovery/pending', r => r.fulfill(json({ pending: [] })));
  await page.route('**/api/cli/*/models*', r => r.fulfill(json({ models: [], source: 'stubbed' })));
  await page.route('**/api/watch-paths', r => r.fulfill(json([preferred])));
  await page.route('**/api/workspaces', r => r.fulfill(json([{
    id: 'WS-1', displayName: 'Workspace', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-07-22T00:00:00Z',
    projects: [{
      id: 'PROJ-1', displayName: projectName, shortCode: 'PLH', workspaceId: 'WS-1',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: preferred.path, archived: false, createdAt: '2026-07-22T00:00:00Z',
    }],
  }])));
  await page.route('**/api/environment', r => r.fulfill(json({
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  })));
  await page.route('**/api/cli/quota', r => r.fulfill(json({
    at: '2026-07-23T01:00:00Z', ttlSeconds: 600, snapshots: [],
  })));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({
    at: '2026-07-23T01:00:00Z', sessions: [],
  })));
  await page.route('**/api/tasks/grouped', r => r.fulfill(json({
    archive: [], autoReview: [], backlog: [], codeNotComplete: [], completed: [],
    failedPickup: [], humanReview: [], orchestratorPrep: [], preparation: [],
    progress: [], ready: [], review: [],
  })));
  await page.route('**/api/tasks/archive**', r => r.fulfill(json({ items: [], total: 0 })));
  await page.route('**/api/runner/status', r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/projects/pipeline-catalogue**', r => r.fulfill(json(CATALOGUE)));
  await page.route('**/api/projects/settings', r => r.fulfill(json({ [projectName]: SETTINGS_PROJECTION })));
  await page.route('**/token-usage/pipeline-cost*', r => r.fulfill(json(fakeCost(projectName))));
  await page.route('**/api/projects/*/pipeline-health', r => r.fulfill(json({
    project: projectName,
    capturedAtUtc: '2026-07-23T01:00:00Z',
    status: 'alarm',
    activeGate: {
      gateRunId: 'gate-night-1', project: projectName, jobId: 'AGT-2183',
      acquiredAtUtc: '2026-07-22T22:30:00Z', elapsedMinutes: 150,
      budgetMinutes: 30, isHanging: true,
    },
    fingerprint: {
      fingerprint: 'lock:9c2f19e4a88c73ab', consecutiveFailures: 3, threshold: 3,
      projects: [projectName, 'Website'], isSystemic: true,
    },
    lanes: [
      { lane: '2-ready', queueCount: 2, completedPerHour: 1, isStalled: false },
      { lane: '3-progress', queueCount: 1, completedPerHour: 1, isStalled: false },
      { lane: '4-auto-review', queueCount: 4, completedPerHour: 0, isStalled: true },
      { lane: '5-human-review', queueCount: 3, completedPerHour: 2, isStalled: false },
    ],
    alerts: [],
  })));

  // Tall viewport so the shell's inner scroll area shows the whole panel
  // (every phase group, each with its per-step token chips) in one shot.
  await page.setViewportSize({ width: 1440, height: 2400 });

  await page.goto(`/#/projects/${projectSlug}/pipeline`, { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });

  const section = page.getByTestId('project-detail-pipeline');
  await expect(section).toBeVisible();
  const health = page.getByTestId('pipeline-health');
  await expect(health).toHaveAttribute('data-status', 'alarm');
  await expect(page.getByTestId('pipeline-health-gate')).toContainText('Gate hanging since 150 min');
  await expect(page.getByTestId('pipeline-health-fingerprint')).toContainText('Systemic gate problem');
  await expect(page.getByTestId('pipeline-health-drain').locator('[data-lane="4-auto-review"]')).toContainText('0/h');

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
  // Per-step token sum from the mocked window, on each row; no bottom total.
  await expect(page.getByTestId('pipeline-step-tokens-core-run')).toContainText('480.0k');
  await expect(page.getByTestId('pipeline-step-tokens-aspect-code-quality')).toContainText('64.0k');
  await expect(page.getByTestId('pipeline-cost')).toHaveCount(0);
  await expect(page.getByTestId('pipeline-cost-total')).toHaveCount(0);

  await setTheme(page, 'light');
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-full--mocked.png'), fullPage: true });
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-page-section--mocked.png') });
  await health.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-health-night-alarms--light--mocked.png') });

  await setTheme(page, 'dark');
  await health.screenshot({ path: path.join(SCREENSHOT_DIR, 'pipeline-health-night-alarms--dark--mocked.png') });
});
