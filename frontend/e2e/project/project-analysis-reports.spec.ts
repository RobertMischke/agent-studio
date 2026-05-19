import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';

/**
 * Project-level Analysis Reports surface (ROADMAP "Analysis Reports and
 * Meta-Actions"). Verifies four states the contract names explicitly:
 *
 * 1. Empty state - no reports yet, the surface still renders with all
 *    manual-trigger buttons and the schedule rows defaulted to disabled.
 * 2. History after a manual trigger - row appears with topic, severity,
 *    parse status, and is openable as a drill-down.
 * 3. Unstructured-report warning - a sidecar-less report renders with the
 *    explicit "unstructured" badge in the list and a banner in the
 *    drill-down. The Markdown body stays visible (the load-bearing rule
 *    of docs/analysis-reports.md - parse failures never hide the body).
 * 4. Manual-trigger flow - clicking a topic button writes a report and the
 *    list refreshes to include it.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOTS = path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-analysis-reports');
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
  // Each test starts from a known-empty Analysis Reports state. The watched
  // project's logs/analysis/<project>/ directory is the durable store; we
  // wipe it (and its sibling) so the first test sees the empty state and
  // later tests start from a known baseline. The directory sits under the
  // workspace root which equals the watched-project path's parent for the
  // local layout. Resolution is best-effort: if the resolution fails we
  // skip the wipe and fall back to an additive baseline.
  const candidates = candidateAnalysisDirs(projectPath, projectName);
  for (const dir of candidates) {
    try {
      if (fs.existsSync(dir)) {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    } catch { /* best-effort */ }
  }
  // Drop the backend's in-memory projection so the wipe is visible without
  // a backend restart. ?refresh=true on the list endpoint invalidates and
  // re-reads from disk.
  try { await api(`/api/analysis/${encodeURIComponent(projectName)}/reports?refresh=true`); }
  catch { /* if the backend is mid-startup, the next test page-load retries */ }
});

test('empty state renders manual triggers and schedule rows', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  const detail = page.getByTestId('project-detail');
  await expect(detail).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-analysis-reports-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();

  await expect(page.getByTestId('project-analysis-reports-empty')).toBeVisible();
  await expect(page.getByTestId('project-analysis-reports-triggers')).toBeVisible();

  // Every topic button is present.
  for (const slug of ['roadmapAlignment', 'queueHealth', 'docsDrift', 'staleJobs', 'tokenSpend', 'qaStatus']) {
    await expect(page.getByTestId(`project-analysis-trigger-${slug}`)).toBeVisible();
  }

  // Schedule defaults: every row reads "disabled".
  for (const slug of ['roadmapAlignment', 'queueHealth', 'docsDrift', 'staleJobs', 'tokenSpend', 'qaStatus']) {
    await expect(page.getByTestId(`project-analysis-schedule-${slug}`)).toHaveValue('disabled');
  }

  await page.screenshot({
    path: `${SCREENSHOTS}/01-empty-state.png`,
    fullPage: true,
  });
});

test('manual trigger writes a report and the list refreshes', async ({ page }) => {
  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-analysis-reports-section');
  await section.scrollIntoViewIfNeeded();
  await expect(section).toBeVisible();

  await expect(page.getByTestId('project-analysis-reports-empty')).toBeVisible();

  // Click the queue-health trigger and wait for the row to appear.
  const trigger = page.getByTestId('project-analysis-trigger-queueHealth');
  await trigger.click();

  const list = page.getByTestId('project-analysis-reports-list');
  await expect(list).toBeVisible({ timeout: 5_000 });
  const row = list.getByTestId('project-analysis-report-row').first();
  await expect(row).toContainText('queueHealth');
  await expect(row).toContainText('manual');

  await page.screenshot({
    path: `${SCREENSHOTS}/02-history-after-trigger.png`,
    fullPage: true,
  });

  // Drill down: clicking the row opens the overlay with the Markdown body.
  await row.click();
  const overlay = page.getByTestId('analysis-report-overlay');
  await expect(overlay).toBeVisible();
  const drilldown = page.getByTestId('analysis-report-drilldown');
  await expect(drilldown).toBeVisible();
  await expect(page.getByTestId('analysis-report-markdown')).toContainText('queueHealth');

  await page.screenshot({
    path: `${SCREENSHOTS}/03-drilldown.png`,
    fullPage: true,
  });

  // Close the drill-down.
  await page.getByTestId('analysis-report-close').click();
  await expect(overlay).toBeHidden();
});

test('unstructured report shows the warning banner without hiding Markdown', async ({ page }) => {
  // Plant an unstructured report directly on disk: Markdown sibling exists,
  // index entry exists, no JSON sidecar. Mirrors the on-disk shape that the
  // contract calls "Unstructured" (parseStatus = Unstructured, sidecar
  // absent). The store's lenient read picks this up on the first list call.
  const dir = primaryAnalysisDir(projectPath, projectName);
  fs.mkdirSync(dir, { recursive: true });
  const reportId = '01HXUNSTRUCT' + Date.now().toString(36).slice(-6).padEnd(8, '0');
  const createdAt = new Date().toISOString();
  const record = {
    reportId,
    createdAt,
    scope: { kind: 'Project', project: projectName },
    producer: { kind: 'Manual', participantId: 'user' },
    trigger: 'Manual',
    topic: 'docsDrift',
    summary: 'Markdown-only docs drift sample. JSON sidecar intentionally absent.',
    severity: 'Warn',
    parseStatus: 'Unstructured',
    references: [],
    followUpTaskSuggestions: [],
    schemaVersion: 1,
  };
  fs.appendFileSync(path.join(dir, 'index.jsonl'), JSON.stringify(record) + '\n');
  fs.writeFileSync(
    path.join(dir, `${reportId}.md`),
    `# docsDrift\n\n**Verdict:** warn — sample drift report.\n\nThis report has no JSON sidecar; the Markdown body is the only artifact.\n`,
    'utf8'
  );

  // Force the in-memory projection to re-read so the planted file is picked up.
  // ?refresh=1 invalidates the cache and re-reads the index from disk.
  await api(`/api/analysis/${encodeURIComponent(projectName)}/reports?refresh=true`);

  await page.goto('/');
  await page.getByTestId(`project-shell-open-${projectName}`).click();
  await expect(page.getByTestId('project-detail')).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('project-analysis-reports-section');
  await section.scrollIntoViewIfNeeded();

  const list = page.getByTestId('project-analysis-reports-list');
  await expect(list).toBeVisible({ timeout: 10_000 });
  const row = list.getByTestId('project-analysis-report-row').first();
  await expect(row).toContainText('docsDrift');
  await expect(page.getByTestId('project-analysis-report-parse-unstructured')).toBeVisible();

  await page.screenshot({
    path: `${SCREENSHOTS}/04-unstructured-list.png`,
    fullPage: true,
  });

  // Drill down: Markdown body stays visible alongside the warning banner.
  await row.click();
  await expect(page.getByTestId('analysis-report-drilldown')).toBeVisible();
  await expect(page.getByTestId('analysis-report-warn-unstructured')).toBeVisible();
  await expect(page.getByTestId('analysis-report-markdown')).toContainText('docsDrift');

  await page.screenshot({
    path: `${SCREENSHOTS}/05-unstructured-drilldown.png`,
    fullPage: true,
  });
});

/**
 * Best-effort enumeration of likely <workspaceRoot>/logs/analysis/<project>/
 * paths for a watched project. The store keys off `IConfiguration["TaskRepository"]`
 * on the backend; the Playwright spec does not have direct access to that
 * config so we walk a few candidate ancestors of the watch path. Wipes apply
 * to all matches; reads use the backend so the resolution mismatch is benign.
 */
function candidateAnalysisDirs(watchPath: string, project: string): string[] {
  const out = new Set<string>();
  const slash = (p: string) => p.replace(/\\/g, '/');
  const norm = slash(watchPath);
  // .../<workspace>/projects/<project> → .../<workspace>/logs/analysis/<project>
  const m = norm.match(/^(.*)\/projects\/[^/]+\/?$/i);
  if (m) out.add(path.join(m[1], 'logs', 'analysis', project));
  // Walk a few levels of parents.
  let cur = path.dirname(watchPath);
  for (let i = 0; i < 4; i++) {
    out.add(path.join(cur, 'logs', 'analysis', project));
    const next = path.dirname(cur);
    if (next === cur) break;
    cur = next;
  }
  return [...out];
}

function primaryAnalysisDir(watchPath: string, project: string): string {
  return candidateAnalysisDirs(watchPath, project)[0];
}

// Suppress an unused-variable warning for BACKEND when the tests don't use
// it directly. Imported so `api` works; eslint tolerates this pattern.
void BACKEND;
