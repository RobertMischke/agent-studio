import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';

/**
 * Project Drift surface, architecture-marble region. Verifies the five
 * states the prompt task names explicitly:
 *
 *   1. no architecture model       (no drift report carrying one)
 *   2. healthy map                 (all elements Info, score band Healthy)
 *   3. warning / critical element  (mixed severities, hot marble visible)
 *   4. element drill-down          (click marble, panel + actions render)
 *   5. invalid drift JSON fallback (planted bad record, error pill renders)
 *
 * Drift reports are append-only and immutable. The spec plants reports
 * directly on disk under `<workspaceRoot>/logs/drift/<project>/` (Markdown +
 * JSON sidecar + index.jsonl entry) and forces the in-memory projection to
 * re-read via `?refresh=true`. No backend restart needed.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOTS = path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-drift-architecture-marble');
fs.mkdirSync(SCREENSHOTS, { recursive: true });

let projectName = '';
let projectPath = '';

test.beforeAll(async () => {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
  projectPath = preferred.path;
});

test.beforeEach(async () => {
  // Wipe drift state so each test starts from a known baseline.
  for (const dir of candidateDriftDirs(projectPath, projectName)) {
    try { if (fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true }); }
    catch { /* best-effort */ }
  }
  try { await api(`/api/drift/${encodeURIComponent(projectName)}/reports?refresh=true`); }
  catch { /* tolerate transient backend startup */ }
});

test('no architecture model: empty state with explanatory copy', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  const detail = page.getByTestId('project-detail');
  await expect(detail).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-drift-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();

  await expect(page.getByTestId('project-drift-empty')).toBeVisible();
  await expect(page.getByTestId('project-drift-map')).toHaveCount(0);

  await page.screenshot({ path: `${SCREENSHOTS}/01-no-architecture-model.png`, fullPage: true });
});

test('healthy map: every element Info, every marble in the info band', async ({ page }) => {
  plantDriftReport(driftDir(projectPath, projectName), {
    overall: 92,
    band: 'Healthy',
    elements: [
      element('api',       'API surface',     85, 'Info', 0.9, 'New'),
      element('runner',    'Project runner',  88, 'Info', 0.85, 'New'),
      element('frontend',  'Web frontend',    91, 'Info', 0.92, 'Accepted'),
      element('schemas',   'Report schemas',  90, 'Info', 0.95, 'New'),
    ],
  });
  await api(`/api/drift/${encodeURIComponent(projectName)}/reports?refresh=true`);

  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-drift-section');
  await section.scrollIntoViewIfNeeded();
  await expect(page.getByTestId('project-drift-map')).toBeVisible();
  await expect(page.getByTestId('project-drift-element-count')).toContainText('4 / 10');

  // Every severity badge says Info.
  for (const id of ['api', 'runner', 'frontend', 'schemas']) {
    await expect(page.getByTestId(`project-drift-severity-${id}`)).toHaveText('Info');
    const marble = page.getByTestId(`project-drift-marble-${id}`);
    await expect(marble).toHaveAttribute('data-severity', 'Info');
  }

  await page.screenshot({ path: `${SCREENSHOTS}/02-healthy-map.png`, fullPage: true });
});

test('warning and critical element: hot marbles render and surface their severity', async ({ page }) => {
  plantDriftReport(driftDir(projectPath, projectName), {
    overall: 48,
    band: 'Warn',
    elements: [
      element('api',       'API surface',     78, 'Info',     0.85, 'New'),
      element('runner',    'Project runner',  42, 'High',     0.7,  'Tracked',
        'Runner state-machine has drifted from ADR-0024. Two endpoints still write job state outside JobTransitionService.'),
      element('frontend',  'Web frontend',    25, 'Critical', 0.9,  'New',
        'Detail panel polls every 1s and re-binds the entire panel. Performance regression risk.'),
      element('schemas',   'Report schemas',  60, 'Warn',     0.55, 'Accepted'),
    ],
  });
  await api(`/api/drift/${encodeURIComponent(projectName)}/reports?refresh=true`);

  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-drift-section');
  await section.scrollIntoViewIfNeeded();
  await expect(page.getByTestId('project-drift-map')).toBeVisible();

  await expect(page.getByTestId('project-drift-severity-runner')).toHaveText('High');
  await expect(page.getByTestId('project-drift-severity-frontend')).toHaveText('Critical');
  await expect(page.getByTestId('project-drift-severity-schemas')).toHaveText('Warn');
  await expect(page.getByTestId('project-drift-marble-frontend')).toHaveAttribute('data-severity', 'Critical');

  await page.screenshot({ path: `${SCREENSHOTS}/03-warn-and-critical.png`, fullPage: true });
});

test('element drill-down: panel renders evidence, guidelines, and action buttons', async ({ page }) => {
  plantDriftReport(driftDir(projectPath, projectName), {
    overall: 55,
    band: 'Warn',
    elements: [
      element('runner', 'Project runner', 45, 'High', 0.78, 'Tracked',
        'Runner state-machine has drifted from ADR-0024.', {
          guidelines: ['Single-writer state machine via JobTransitionService.'],
          allowedDependencies: ['Services/Jobs/*', 'Services/TaskAccess/*'],
          evidenceRefs: ['docs/architecture-decisions.md#adr-0024', 'backend/Services/Jobs/JobTransitionService.cs:1'],
          followUps: ['Reconcile RunnerEndpoints with JobTransitionService.'],
        }),
      element('api', 'API surface', 80, 'Info', 0.9, 'New'),
    ],
  });
  await api(`/api/drift/${encodeURIComponent(projectName)}/reports?refresh=true`);

  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-drift-section');
  await section.scrollIntoViewIfNeeded();
  await expect(page.getByTestId('project-drift-map')).toBeVisible();

  await page.getByTestId('project-drift-marble-runner').click();
  const panel = page.getByTestId('project-drift-drilldown');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Project runner');
  await expect(panel.getByText('45 (High)')).toBeVisible();

  // Evidence list is present.
  await expect(page.getByTestId('project-drift-evidence')).toBeVisible();
  await expect(page.getByTestId('project-drift-evidence')).toContainText('JobTransitionService.cs');

  // Action buttons are present and enabled.
  const actions = page.getByTestId('project-drift-actions');
  await expect(actions).toBeVisible();
  await expect(page.getByTestId('project-drift-action-analyze-runner')).toBeVisible();
  await expect(page.getByTestId('project-drift-action-followup-runner')).toBeVisible();
  await expect(page.getByTestId('project-drift-action-mark-tracked-runner')).toBeVisible();
  await expect(page.getByTestId('project-drift-action-mark-accepted-runner')).toBeVisible();
  await expect(page.getByTestId('project-drift-action-mark-ignored-runner')).toBeVisible();

  await page.screenshot({ path: `${SCREENSHOTS}/04-element-drilldown.png`, fullPage: true });

  // Mark accepted: status pill flips to Accepted via the backend's element-state sidecar.
  await page.getByTestId('project-drift-action-mark-accepted-runner').click();
  await expect(page.getByTestId('project-drift-status-runner')).toHaveText('Accepted', { timeout: 5_000 });
  await expect(page.getByTestId('project-drift-action-msg')).toContainText('Marked');
});

test('invalid drift JSON fallback: malformed response surfaces the error pill', async ({ page }) => {
  // Intercept the architecture endpoint and return malformed JSON. The
  // frontend's error path catches the parse failure and renders the
  // `project-drift-error` banner without ever rendering the marble grid.
  await page.route(`**/api/drift/${encodeURIComponent(projectName)}/architecture`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: '{"model": this is not valid JSON',
    });
  });

  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-drift-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();

  await expect(page.getByTestId('project-drift-error')).toBeVisible({ timeout: 5_000 });
  await expect(page.getByTestId('project-drift-map')).toHaveCount(0);
  await expect(page.getByTestId('project-drift-retry')).toBeVisible();

  await page.screenshot({ path: `${SCREENSHOTS}/05-invalid-json-fallback.png`, fullPage: true });
});

// ----------------------------------------------------------------------
// Helpers
// ----------------------------------------------------------------------

interface PlantedElement {
  elementId: string;
  label: string;
  expectedRole: string;
  score: number;
  severity: string;
  sourceCoverage: number;
  status: string;
  summary?: string;
  evidenceRefs: string[];
  guidelines?: string[];
  allowedDependencies?: string[];
  followUpTaskSuggestions?: string[];
}

function element(
  id: string,
  label: string,
  score: number,
  severity: string,
  sourceCoverage: number,
  status: string,
  summary?: string,
  extras?: { guidelines?: string[]; allowedDependencies?: string[]; evidenceRefs?: string[]; followUps?: string[] }
): PlantedElement {
  return {
    elementId: id,
    label,
    expectedRole: `Owns ${label.toLowerCase()}.`,
    score,
    severity,
    sourceCoverage,
    status,
    summary,
    evidenceRefs: extras?.evidenceRefs ?? [`docs/${id}.md`],
    guidelines: extras?.guidelines,
    allowedDependencies: extras?.allowedDependencies,
    followUpTaskSuggestions: extras?.followUps,
  };
}

function plantDriftReport(dir: string, opts: { overall: number; band: string; elements: PlantedElement[] }): void {
  fs.mkdirSync(dir, { recursive: true });
  const reportId = '01HXARCH' + Date.now().toString(36).slice(-6).padEnd(8, '0');
  const createdAt = new Date().toISOString();
  const record = {
    reportId,
    project: projectName,
    createdAt,
    trigger: 'Manual',
    scope: { kind: 'Project', sourceRefs: ['docs/architecture-decisions.md'] },
    overallScore: opts.overall,
    scoreBand: opts.band,
    summary: 'Architecture marble surface fixture (planted by Playwright spec).',
    dimensions: [
      {
        type: 'Architecture',
        score: opts.overall,
        severity: 'Info',
        confidence: 0.7,
        sourceCoverage: 0.8,
        status: 'New',
        summary: 'Architecture-only fixture for the marble surface.',
        evidenceRefs: ['docs/architecture-decisions.md'],
        recommendedActions: [],
      },
    ],
    followUpTaskSuggestions: [],
    schemaVersion: 1,
    architectureModel: {
      modelId: 'fixture-model-1',
      title: 'Project architecture map (fixture)',
      sourceRef: 'docs/architecture-decisions.md',
      elements: opts.elements,
    },
  };
  fs.appendFileSync(path.join(dir, 'index.jsonl'), JSON.stringify(record) + '\n');
  fs.writeFileSync(path.join(dir, `${reportId}.json`), JSON.stringify(record, null, 2));
  fs.writeFileSync(path.join(dir, `${reportId}.md`), '# Architecture marble fixture\n\nPlanted by Playwright.\n', 'utf8');
}

/**
 * Best-effort enumeration of likely <workspaceRoot>/logs/drift/<project>/
 * paths for a watched project. Mirrors the candidate-walk used by
 * project-analysis-reports.spec.ts so the wipe/plant paths stay consistent
 * across the local devspace and the agent-taskboard-workspace layout.
 */
function candidateDriftDirs(watchPath: string, project: string): string[] {
  const out = new Set<string>();
  const slash = (p: string) => p.replace(/\\/g, '/');
  const norm = slash(watchPath);
  const m = norm.match(/^(.*)\/projects\/[^/]+\/?$/i);
  if (m) out.add(path.join(m[1], 'logs', 'drift', project));
  let cur = path.dirname(watchPath);
  for (let i = 0; i < 4; i++) {
    out.add(path.join(cur, 'logs', 'drift', project));
    const next = path.dirname(cur);
    if (next === cur) break;
    cur = next;
  }
  return [...out];
}

function driftDir(watchPath: string, project: string): string {
  return candidateDriftDirs(watchPath, project)[0];
}

void BACKEND;
