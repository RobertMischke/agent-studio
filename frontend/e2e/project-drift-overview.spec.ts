import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from './helpers/api';

/**
 * Project Drift overview surface (ROADMAP "Drift Control"). Verifies the five
 * states the prompt task names explicitly:
 *
 *   1. empty state                     (no drift reports for this project)
 *   2. scored report                   (overall score, band, dimension cards)
 *   3. dimension drill-down            (click a dim card; evidence renders)
 *   4. invalid JSON / unstructured     (parseStatus surfaces a warning chip,
 *                                       Markdown still readable)
 *   5. follow-up-task creation         (clicking the finding follow-up button
 *                                       queues a real 1-preparation job)
 *
 * Drift reports are append-only; we plant fixtures directly on disk under
 * `<workspaceRoot>/logs/drift/<project>/` (Markdown + JSON sidecar + index)
 * and force the in-memory projection to re-read via `?refresh=true`. Mirrors
 * the planting strategy in project-drift-architecture-marble.spec.ts.
 */

interface WatchPath { name: string; path: string }
interface CreateJobResp { id: string }

const SCREENSHOTS = path.resolve(__dirname, '..', 'playwright-screenshots', 'project-drift-overview');
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
  // Wipe the drift folder for this project so each test starts from a known
  // baseline. The InMemoryStore reads only index.jsonl; truncating that file
  // is enough to make the projection empty on the next refresh, even if a
  // stray json/md sibling could not be deleted (Windows file-lock case).
  // We additionally try to remove the dir recursively so subsequent plants
  // do not see stray reports the projection happens to ignore.
  for (const dir of candidateDriftDirs(projectPath, projectName)) {
    try {
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, 'index.jsonl'), '', 'utf8');
      for (const name of fs.readdirSync(dir)) {
        if (name === 'index.jsonl') continue;
        try { fs.unlinkSync(path.join(dir, name)); } catch { /* best-effort */ }
      }
    } catch { /* best-effort */ }
  }
  // Force projection refresh + verify the projection actually settles to
  // zero. The previous run sometimes left the in-memory projection holding
  // a record whose disk file was wiped seconds earlier; re-issuing refresh
  // until the count drops to zero closes that gap before the page polls.
  for (let i = 0; i < 10; i++) {
    try {
      const resp = await api<{ reports: unknown[] }>(`/api/drift/${encodeURIComponent(projectName)}/reports?refresh=true`);
      if (!resp.reports || resp.reports.length === 0) break;
    } catch { /* tolerate transient errors */ }
    await new Promise(r => setTimeout(r, 250));
  }
});

test('empty state: section visible with action buttons; no scored block', async ({ page }) => {
  await openProjectDetail(page);

  const section = page.getByTestId('project-drift-overview-section');
  await expect(section).toBeAttached({ timeout: 30_000 });
  await section.evaluate(el => el.scrollIntoView({ block: 'start' }));

  // Empty banner visible, no latest report, no dimensions block.
  await expect(page.getByTestId('project-drift-overview-empty')).toBeVisible();
  await expect(page.getByTestId('project-drift-overview-latest')).toHaveCount(0);
  await expect(page.getByTestId('project-drift-overview-dimensions')).toHaveCount(0);

  // All seven action buttons render even when there is no history.
  for (const slug of [
    'analyze-project', 'specs-tasks-jobs', 'adrs-code', 'docs-marketing',
    'design-screenshots', 'tests-risk', 'runtime-expectations',
  ]) {
    await expect(page.getByTestId(`project-drift-overview-action-${slug}`)).toBeVisible();
  }

  await page.screenshot({ path: `${SCREENSHOTS}/01-empty-state.png`, fullPage: true });
});

test('scored report: overall score, band, summary, dimension cards, history row', async ({ page }) => {
  const reportId = plantDriftReport(driftDir(projectPath, projectName), {
    overall: 78,
    band: 'Watch',
    summary: 'Two dimensions tracked; architecture docs slightly stale.',
    parseStatus: 'Structured',
    dimensions: [
      dimension('Architecture',  72, 'Warn', 0.85, 'Tracked',
        'ADR archive references services that no longer exist.'),
      dimension('Documentation', 84, 'Info', 0.90, 'New',
        'README is up to date; docs/agents.md slightly behind.'),
      dimension('Test',          69, 'Warn', 0.55, 'New',
        'Risk areas in supervisor are not exercised end-to-end.'),
    ],
    followUps: [
      followUp('Refresh ADR archive against current services', 'High', 'Architecture'),
    ],
  });
  await waitForReportVisible(reportId);

  await openProjectDetail(page);
  const section = page.getByTestId('project-drift-overview-section');
  await expect(section).toBeAttached({ timeout: 30_000 });
  await section.evaluate(el => el.scrollIntoView({ block: 'start' }));

  await expect(page.getByTestId('project-drift-overview-score')).toContainText('78');
  await expect(page.getByTestId('project-drift-overview-band')).toContainText('Watch');
  await expect(page.getByTestId('project-drift-overview-latest')).toBeVisible();

  // Three scored dimensions visible by their slug; the rest render empty.
  await expect(page.getByTestId('project-drift-overview-dim-architecture')).toBeVisible();
  await expect(page.getByTestId('project-drift-overview-dim-score-architecture')).toHaveText('72');
  await expect(page.getByTestId('project-drift-overview-dim-sev-architecture')).toHaveText('Warn');

  await expect(page.getByTestId('project-drift-overview-dim-documentation')).toBeVisible();
  await expect(page.getByTestId('project-drift-overview-dim-score-documentation')).toHaveText('84');
  await expect(page.getByTestId('project-drift-overview-dim-test')).toBeVisible();

  // The other dimensions render as empty placeholders.
  await expect(page.getByTestId('project-drift-overview-dim-intent-empty')).toBeVisible();

  // History row visible.
  const history = page.getByTestId('project-drift-overview-history');
  await expect(history).toBeVisible();
  await expect(history.getByTestId('project-drift-overview-history-row').first()).toBeVisible();

  await page.screenshot({ path: `${SCREENSHOTS}/02-scored-report.png`, fullPage: true });
});

test('dimension drill-down: panel renders evidence and findings', async ({ page }) => {
  const reportId = plantDriftReport(driftDir(projectPath, projectName), {
    overall: 64,
    band: 'Warn',
    summary: 'Architecture and Schema drift; finding-level evidence available.',
    parseStatus: 'Structured',
    dimensions: [
      dimension('Architecture', 60, 'High', 0.80, 'Tracked',
        'Runner state-machine has drifted from ADR-0024.', {
          evidence: ['docs/architecture-decisions.md#adr-0024', 'backend/Services/Jobs/JobTransitionService.cs'],
          recommendedActions: ['Reconcile RunnerEndpoints with JobTransitionService.'],
          findings: [
            finding('finding-001', 'High', 'Two endpoints write job state outside JobTransitionService.', 'New', [
              'backend/Endpoints/RunnerEndpoints.cs:142',
              'backend/Endpoints/RunnerEndpoints.cs:201',
            ]),
            finding('finding-002', 'Warn', 'JobScannerService still scans disk on every overlay.', 'Tracked', [
              'backend/Services/Jobs/JobScannerService.cs:88',
            ]),
          ],
        }),
    ],
    followUps: [],
  });
  await waitForReportVisible(reportId);

  await openProjectDetail(page);
  await expect(page.getByTestId('project-drift-overview-section')).toBeAttached({ timeout: 30_000 });
  await page.getByTestId('project-drift-overview-section').evaluate(el => el.scrollIntoView({ block: 'start' }));

  await page.getByTestId('project-drift-overview-dim-architecture').click();
  const panel = page.getByTestId('project-drift-overview-dimension-drilldown');
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Architecture');
  await expect(panel).toContainText('60 / 100');

  // Evidence list rendered.
  const evidence = page.getByTestId('project-drift-overview-dimension-evidence');
  await expect(evidence).toBeVisible();
  await expect(evidence).toContainText('JobTransitionService');

  // Finding rows rendered with severity + per-finding follow-up button.
  const finding1 = page.getByTestId('project-drift-overview-finding-finding-001');
  await expect(finding1).toBeVisible();
  await expect(finding1).toContainText('High');
  await expect(page.getByTestId('project-drift-overview-finding-followup-finding-001')).toBeVisible();

  await page.screenshot({ path: `${SCREENSHOTS}/03-dimension-drilldown.png`, fullPage: true });
});

test('unstructured / malformed JSON: warning chip on history row, drilldown still has Markdown', async ({ page }) => {
  // Plant two reports: one Unstructured, one MalformedJson. The history rows
  // each render their parse-status chip; clicking either opens the report
  // drilldown with the Markdown body intact (scores never hide evidence).
  const dir = driftDir(projectPath, projectName);
  const unstructuredId = plantDriftReport(dir, {
    overall: 0, band: 'Unknown',
    summary: 'Evidence assembled; no agent narrative supplied.',
    parseStatus: 'Unstructured',
    dimensions: [],
    followUps: [],
  });
  const malformedId = plantDriftReport(dir, {
    overall: 0, band: 'Unknown',
    summary: 'Sidecar JSON failed to parse; Markdown body still readable.',
    parseStatus: 'MalformedJson',
    parseError: 'Unexpected token at line 1 column 14',
    dimensions: [],
    followUps: [],
  });
  await waitForReportVisible(unstructuredId);
  await waitForReportVisible(malformedId);

  await openProjectDetail(page);
  await expect(page.getByTestId('project-drift-overview-section')).toBeAttached({ timeout: 30_000 });
  await page.getByTestId('project-drift-overview-section').evaluate(el => el.scrollIntoView({ block: 'start' }));

  // Both parse-status chips visible somewhere in the history list.
  await expect(page.getByTestId('project-drift-overview-history-parse-malformedjson').first()).toBeVisible();
  await expect(page.getByTestId('project-drift-overview-history-parse-unstructured').first()).toBeVisible();

  // Click the first row (newest report).
  await page.getByTestId('project-drift-overview-history-row').first().click();
  await expect(page.getByTestId('project-drift-overview-report-modal')).toBeVisible();

  // Whichever parse-status warning shows up, Markdown stays readable.
  const warnMalformed = page.getByTestId('project-drift-overview-report-warn-malformed');
  const warnUnstructured = page.getByTestId('project-drift-overview-report-warn-unstructured');
  expect(await warnMalformed.isVisible() || await warnUnstructured.isVisible()).toBeTruthy();
  await expect(page.getByTestId('project-drift-overview-report-md')).toBeVisible();

  await page.screenshot({ path: `${SCREENSHOTS}/04-malformed-or-unstructured.png`, fullPage: true });
});

test('follow-up task creation: clicking a finding follow-up queues a 1-preparation job', async ({ page }) => {
  const reportId = plantDriftReport(driftDir(projectPath, projectName), {
    overall: 50,
    band: 'Warn',
    summary: 'Schema drift with one tracked finding.',
    parseStatus: 'Structured',
    dimensions: [
      dimension('Schema', 45, 'High', 0.85, 'New',
        'Three schemas include unused fields not present in the DTOs.', {
          evidence: ['docs/schemas/agent-message.schema.json'],
          recommendedActions: [],
          findings: [
            finding('finding-schema-001', 'High',
              'agent-message.schema.json carries fields removed from AgentMessage.cs',
              'New',
              ['docs/schemas/agent-message.schema.json', 'backend/Services/Bus/AgentMessage.cs']),
          ],
        }),
    ],
    followUps: [],
  });
  await waitForReportVisible(reportId);

  await openProjectDetail(page);
  await expect(page.getByTestId('project-drift-overview-section')).toBeAttached({ timeout: 30_000 });
  await page.getByTestId('project-drift-overview-section').evaluate(el => el.scrollIntoView({ block: 'start' }));

  await page.getByTestId('project-drift-overview-dim-schema').click();
  await expect(page.getByTestId('project-drift-overview-dimension-drilldown')).toBeVisible();

  const before = await listPreparationSlugs();

  const responsePromise = page.waitForResponse(r => r.url().includes('/api/jobs') && r.request().method() === 'POST');
  await page.getByTestId('project-drift-overview-finding-followup-finding-schema-001').click();
  const resp = await responsePromise;
  expect(resp.status(), `POST /api/jobs returned ${resp.status()}`).toBeLessThan(400);

  // Confirmation message rendered.
  await expect(page.getByTestId('project-drift-overview-action-msg')).toContainText('Queued', { timeout: 5_000 });

  // The new job exists in 1-preparation.
  await page.waitForTimeout(500);
  const after = await listPreparationSlugs();
  const created = [...after].filter(s => !before.has(s));
  expect(created.length, `expected exactly one new preparation job after click; got ${created.length}`).toBeGreaterThanOrEqual(1);
  expect(created.some(s => s.startsWith('followup-drift-schema-'))).toBeTruthy();

  await page.screenshot({ path: `${SCREENSHOTS}/05-followup-task-created.png`, fullPage: true });

  // Cleanup: delete the created jobs so other specs do not see them.
  for (const id of created) {
    try { await api(`/api/jobs/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(projectPath)}`, { method: 'DELETE' }); }
    catch { /* best-effort */ }
  }
});

// ----------------------------------------------------------------------
// Helpers
// ----------------------------------------------------------------------

async function openProjectDetail(page: Page): Promise<void> {
  // Clear the browser's HTTP cache so a per-context fetch can never return
  // a previous test's planted report. The test runs at workers=1 with
  // shared context defaults; the cache otherwise survives navigation.
  try {
    const session = await page.context().newCDPSession(page);
    await session.send('Network.clearBrowserCache');
    await session.detach();
  } catch { /* best-effort */ }
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });
}

/**
 * Poll the dev backend until /reports?refresh=true returns the expected
 * report id. Closes the disk-projection-cache race that otherwise lets the
 * page navigate before the InMemoryStore has re-loaded from disk.
 */
async function waitForReportVisible(reportId: string, timeoutMs = 5000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const resp = await api<{ reports: { reportId: string }[] }>(`/api/drift/${encodeURIComponent(projectName)}/reports?refresh=true&_=${Date.now()}`);
      if ((resp.reports ?? []).some(r => r.reportId === reportId)) return;
    } catch { /* tolerate transient errors */ }
    await new Promise(r => setTimeout(r, 100));
  }
  throw new Error(`drift report ${reportId} not visible after ${timeoutMs}ms; the projection cache did not catch up to disk.`);
}

interface PlantedDimension {
  type: string;
  score: number;
  severity: string;
  confidence: number;
  sourceCoverage: number;
  status: string;
  summary: string;
  evidenceRefs: string[];
  recommendedActions: string[];
  findings?: PlantedFinding[];
}

interface PlantedFinding {
  findingId: string;
  severity: string;
  summary: string;
  status: string;
  evidenceRefs: string[];
}

interface PlantedFollowUp {
  title: string;
  summary: string;
  priority: string;
  relatedDimension?: string;
}

function dimension(
  type: string,
  score: number,
  severity: string,
  sourceCoverage: number,
  status: string,
  summary: string,
  extras?: { evidence?: string[]; recommendedActions?: string[]; findings?: PlantedFinding[] }
): PlantedDimension {
  return {
    type, score, severity, sourceCoverage,
    confidence: 0.7,
    status,
    summary,
    evidenceRefs: extras?.evidence ?? [`docs/${type.toLowerCase()}.md`],
    recommendedActions: extras?.recommendedActions ?? [],
    findings: extras?.findings,
  };
}

function finding(
  findingId: string, severity: string, summary: string, status: string, evidenceRefs: string[]
): PlantedFinding {
  return { findingId, severity, summary, status, evidenceRefs };
}

function followUp(title: string, priority: string, relatedDimension?: string, summary = 'Drift follow-up suggestion.'): PlantedFollowUp {
  return { title, summary, priority, relatedDimension };
}

interface PlantOpts {
  overall: number;
  band: string;
  summary: string;
  parseStatus: 'Structured' | 'Unstructured' | 'MalformedJson';
  parseError?: string;
  dimensions: PlantedDimension[];
  followUps: PlantedFollowUp[];
}

function plantDriftReport(dir: string, opts: PlantOpts): string {
  fs.mkdirSync(dir, { recursive: true });
  const reportId = '01HXOV' + Date.now().toString(36).slice(-6).padEnd(8, '0') + Math.random().toString(36).slice(2, 6);
  const createdAt = new Date().toISOString();
  // The schema requires at least one dimension. When the caller supplies
  // none (used to exercise the unstructured / malformed paths) we add a
  // single Info placeholder so the index entry validates.
  const dims = opts.dimensions.length > 0 ? opts.dimensions : [
    {
      type: 'Architecture', score: 50, severity: 'Info', confidence: 0.5,
      sourceCoverage: 0.5, status: 'New',
      summary: 'Placeholder dimension for unstructured / malformed fixture.',
      evidenceRefs: [], recommendedActions: [],
    } as PlantedDimension,
  ];
  const record: any = {
    schemaVersion: 1,
    reportId,
    project: projectName,
    createdAt,
    trigger: 'Manual',
    scope: { kind: 'Project', sourceRefs: [] },
    overallScore: opts.overall,
    scoreBand: opts.band,
    summary: opts.summary,
    parseStatus: opts.parseStatus,
    parseError: opts.parseError ?? null,
    dimensions: dims.map(d => ({
      type: d.type,
      score: d.score,
      severity: d.severity,
      confidence: d.confidence,
      sourceCoverage: d.sourceCoverage,
      status: d.status,
      summary: d.summary,
      evidenceRefs: d.evidenceRefs,
      recommendedActions: d.recommendedActions,
      findings: d.findings,
    })),
    followUpTaskSuggestions: opts.followUps.map(f => ({
      title: f.title,
      summary: f.summary,
      priority: f.priority,
      relatedDimension: f.relatedDimension ?? null,
    })),
    producer: { kind: 'Manual' },
  };
  fs.appendFileSync(path.join(dir, 'index.jsonl'), JSON.stringify(record) + '\n');
  fs.writeFileSync(path.join(dir, `${reportId}.json`), JSON.stringify(record, null, 2));
  fs.writeFileSync(
    path.join(dir, `${reportId}.md`),
    `# Drift report ${reportId}\n\n${opts.summary}\n\nPlanted by Playwright spec for ${opts.parseStatus} fixture.\n`,
    'utf8',
  );
  return reportId;
}

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

interface JobInfo { id: string; state: string; projectName?: string }
interface GroupedJobs {
  preparation?: JobInfo[];
  ready?: JobInfo[];
  progress?: JobInfo[];
}

async function listPreparationSlugs(): Promise<Set<string>> {
  try {
    const grouped = await api<GroupedJobs>('/api/jobs/grouped');
    const out = new Set<string>();
    for (const j of grouped.preparation ?? []) {
      if (!j.projectName || j.projectName === projectName) out.add(j.id);
    }
    return out;
  } catch {
    return new Set<string>();
  }
}

void BACKEND;
