import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api, BACKEND } from '../helpers/api';

/**
 * Wiki Pulse landing view (PULSE-1). The wiki opens on the generated Pulse view
 * (change feed + inbox + drift grade bar), not a page. This spec drives the real
 * app but overlays a deterministic `/wiki/pulse` payload via route interception
 * (labelled --mocked) so the assertions and the screenshot are stable regardless
 * of the picked project's live git state. Navigation uses the deep-link hash
 * contract (`#/projects/<slug>/wiki`).
 *
 * Screenshots land in the orchestrator job results dir when
 * PROJECT_WIKI_RESULTS_DIR is set; otherwise a sibling of the spec.
 */

interface WatchPath { name: string; path: string }
interface WikiTreeNodeFixture { type: 'folder' | 'md' | 'html' | 'json'; children?: WikiTreeNodeFixture[] }
interface WikiTreeFixture { exists: boolean; root: WikiTreeNodeFixture[] }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_WIKI_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'wiki-pulse');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function countWikiDocs(nodes: readonly WikiTreeNodeFixture[] = []): number {
  return nodes.reduce((count, node) =>
    node.type === 'folder' ? count + countWikiDocs(node.children ?? []) : count + 1, 0);
}

const PULSE_FIXTURE = {
  projectName: 'demo',
  baseDir: '/repo/docs',
  exists: true,
  generatedAtUtc: '2026-07-10T09:00:00Z',
  feed: {
    available: true,
    reason: null,
    items: [
      {
        relPath: 'engineering-workstream/10-current-development-state/pulse.md',
        title: 'Pulse landing view', author: 'Robert Mischke',
        authorDateUtc: new Date(Date.now() - 2 * 3600_000).toISOString(),
        sha: 'aaaaaaa', shortSha: 'aaaaaaa', subject: 'AGT-2014 land pulse',
        frameAreaSlug: '10-current-development-state', frameAreaTitle: 'Current Development State',
        taskKey: 'AGT-2014',
      },
      {
        relPath: 'engineering-workstream/30-system-knowledge/relocation.md',
        title: 'Wiki checkout relocation', author: 'Claude Opus 4.8',
        authorDateUtc: new Date(Date.now() - 26 * 3600_000).toISOString(),
        sha: 'bbbbbbb', shortSha: 'bbbbbbb', subject: 'AGT-1984 relocate wiki',
        frameAreaSlug: '30-system-knowledge', frameAreaTitle: 'System Knowledge',
        taskKey: 'AGT-1984',
      },
    ],
  },
  inbox: {
    available: true,
    reason: null,
    count: 2,
    items: [
      { relPath: 'scratch-idea.md', title: 'Scratch idea', type: 'md', reason: 'Loose page at the wiki root - not filed under a category.' },
      { relPath: 'engineering-workstream/migration-jots.md', title: 'Migration jots', type: 'md', reason: 'Inside the Workstream frame but not filed under one of the five areas.' },
    ],
  },
  drift: {
    available: true,
    reason: null,
    overallGrade: 'Stale',
    areas: [
      { slug: '10-current-development-state', title: 'Current Development State', grade: 'Aging', pageCount: 2, gradedPageCount: 2, worstCommitCount: 12, freshCount: 1, agingCount: 1, staleCount: 0 },
      { slug: '20-development-signals', title: 'Development Signals', grade: 'Fresh', pageCount: 1, gradedPageCount: 1, worstCommitCount: 2, freshCount: 1, agingCount: 0, staleCount: 0 },
      { slug: '30-system-knowledge', title: 'System Knowledge', grade: 'Stale', pageCount: 1, gradedPageCount: 1, worstCommitCount: 73, freshCount: 0, agingCount: 0, staleCount: 1 },
      { slug: '40-decision-log', title: 'Decision Log', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
      { slug: '50-workstream-log', title: 'Workstream Log', grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 },
    ],
    counts: { fresh: 2, aging: 1, stale: 1, graded: 4 },
  },
  critical: {
    available: true,
    reason: null,
    count: 2,
    overallGrade: 'D',
    items: [
      { relPath: 'engineering-workstream/30-system-knowledge/relocation.md', title: 'Wiki checkout relocation', grade: 'D', assessment: 'Describes an old checkout layout; likely outdated.', gradedAt: '2026-07-10T09:00:00Z', model: 'claude-sonnet-5', reportPath: 'engineering-workstream/30-system-knowledge/relocation.md.report.html', frameAreaTitle: 'System Knowledge' },
      { relPath: 'scratch-idea.md', title: 'Scratch idea', grade: 'C', assessment: 'Thin, unfiled scratch note with gaps.', gradedAt: '2026-07-10T09:00:00Z', model: 'claude-sonnet-5', reportPath: 'scratch-idea.md.report.html', frameAreaTitle: null },
    ],
  },
  warnings: {
    available: true,
    reason: null,
    count: 2,
    items: [
      { kind: 'human-action', title: 'Runner restart loop', detail: 'Development signal is active.', humanAction: 'Inspect the latest failed resume before reissuing.', relPath: 'engineering-workstream/20-development-signals/restart.md', status: 'active' },
      { kind: 'dead-link', title: 'Dead link in operator guide', detail: '../missing-runbook.md', humanAction: 'Repair or remove this internal link.', relPath: 'operations/operator-guide.md', status: null },
    ],
  },
  activity: {
    available: true,
    reason: null,
    runs: [{ taskKey: 'AGT-2015', lane: '3-progress', startedAtUtc: new Date(Date.now() - 43 * 60_000).toISOString(), docsFilesChanged: 3 }],
    collector: { ranAtUtc: new Date(Date.now() - 3 * 3600_000).toISOString(), status: 'ok', error: null, merges: 0, condensations: 0 },
    curator: { ranAtUtc: new Date(Date.now() - 6 * 3600_000).toISOString(), status: 'ok', error: null, merges: 2, condensations: 1 },
  },
};

const MAINTENANCE_MODEL = { cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null };
const GRADING_STATUS_DONE = {
  status: {
    projectName: 'demo', runId: 'wg-e2e', state: 'completed', cliType: 'claude', model: 'claude-sonnet-5',
    thinkingLevel: null, force: false, total: 12, processed: 12, graded: 10, skipped: 2, failed: 0,
    critical: 2, currentRelPath: null, startedAtUtc: '2026-07-10T09:00:00Z', completedAtUtc: '2026-07-10T09:02:00Z',
    error: null, recent: [],
  },
};

/** Mocks the grading trigger's seed endpoints so the surface is deterministic. */
async function mockGradingContext(page: import('@playwright/test').Page): Promise<void> {
  await page.route('**/api/cli/maintenance-model', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(MAINTENANCE_MODEL) }));
  await page.route('**/wiki/grading/status**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GRADING_STATUS_DONE) }));
}

async function proxyBackend(page: import('@playwright/test').Page): Promise<void> {
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    const target = `${BACKEND}${url.pathname}${url.search}`;
    let lastError: unknown;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const response = await route.fetch({ url: target });
        await route.fulfill({ response });
        return;
      } catch (error) {
        lastError = error;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }
    throw lastError;
  });
}

test.describe('Wiki Pulse landing view (PULSE-2)', () => {
  let projectName = '';

  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  test.beforeAll(async () => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);
    const prioritized = [
      ...paths.filter(p => /agent.?software|agent.?studio|agent.?task/i.test(p.name)),
      ...paths,
    ];
    const candidates = Array.from(new Map(prioritized.map(p => [p.name, p])).values());
    for (const candidate of candidates) {
      const tree = await api<WikiTreeFixture>(`/api/projects/${encodeURIComponent(candidate.name)}/wiki/tree`);
      if (tree.exists && countWikiDocs(tree.root) > 0) { projectName = candidate.name; break; }
    }
    expect(projectName, 'expected a project with a populated docs/wiki tree').not.toBe('');
  });

  test('opens on the generated Pulse view with feed, inbox, and drift grade bar', async ({ page }) => {
    await proxyBackend(page);
    // Overlay a deterministic Pulse payload so the surface is stable (--mocked).
    await page.route('**/wiki/pulse**', route =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(PULSE_FIXTURE) }));
    await mockGradingContext(page);

    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);

    const pulse = page.getByTestId('project-wiki-pulse');
    await expect(pulse).toBeVisible({ timeout: 10_000 });

    // Drift grade bar: five areas, worst grade elevated to the overall chip.
    await expect(page.getByTestId('project-wiki-pulse-overall')).toContainText('Stale');
    await expect(page.getByTestId('project-wiki-pulse-area-30-system-knowledge')).toContainText('Stale');
    await expect(page.getByTestId('project-wiki-pulse-area-40-decision-log')).toContainText('Empty');

    // Change feed: frame-area badge + task key on a row.
    await expect(page.getByTestId('project-wiki-pulse-task-engineering-workstream/10-current-development-state/pulse.md'))
      .toContainText('AGT-2014');
    await expect(page.getByTestId('project-wiki-pulse-area-badge-engineering-workstream/10-current-development-state/pulse.md'))
      .toContainText('Current Development State');

    // Inbox: two unfiled pages needing sorting.
    await expect(page.getByTestId('project-wiki-pulse-inbox-open-scratch-idea.md')).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-inbox')).toContainText('Needs sorting');

    await expect(page.getByTestId('project-wiki-pulse-warnings')).toContainText('Inspect the latest failed resume');
    await expect(page.getByTestId('project-wiki-pulse-live-AGT-2015')).toContainText('3 docs files changed');
    await expect(page.getByTestId('project-wiki-pulse-collector-run')).toContainText('ok');
    await expect(page.getByTestId('project-wiki-pulse-curator-run')).toContainText('2 merges · 1 condensations');

    // No horizontal overflow on the landing surface.
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    expect(overflow).toBe(false);

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'wiki-pulse-warnings-in-progress--mocked.png'), fullPage: true });
  });

  test('degrades to labelled empty states when a source is unavailable', async ({ page }) => {
    await proxyBackend(page);
    await page.route('**/wiki/pulse**', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        ...PULSE_FIXTURE,
        feed: { available: true, reason: 'No recent edits in git history.', items: [] },
        inbox: { available: true, reason: null, count: 0, items: [] },
        drift: { available: true, reason: 'No knowledge pages filed under the Workstream frame yet.', overallGrade: 'Empty',
          areas: PULSE_FIXTURE.drift.areas.map(a => ({ ...a, grade: 'Empty', pageCount: 0, gradedPageCount: 0, worstCommitCount: 0, freshCount: 0, agingCount: 0, staleCount: 0 })),
          counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
      }),
    }));

    await mockGradingContext(page);
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-pulse')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-pulse-feed-empty')).toContainText('No recent edits');
    await expect(page.getByTestId('project-wiki-pulse-inbox-empty')).toContainText('Inbox clear');
    await expect(page.getByTestId('project-wiki-pulse-drift-empty')).toContainText('No knowledge pages filed');
  });

  test('shows the grading trigger and critical pages (AGT-2051)', async ({ page }) => {
    await proxyBackend(page);
    await page.route('**/wiki/pulse**', route =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(PULSE_FIXTURE) }));
    await mockGradingContext(page);

    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-pulse')).toBeVisible({ timeout: 10_000 });

    // The grading trigger: a model dropdown + a "Grade all pages" button.
    const grading = page.getByTestId('project-wiki-pulse-grading');
    await expect(grading).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-grade-start')).toBeVisible();
    // The last run's outcome is summarised (10 graded, 2 critical).
    await expect(page.getByTestId('project-wiki-pulse-grade-state')).toContainText('critical');

    // Critical pages: worst-first (D before C), click-through targets present.
    const critical = page.getByTestId('project-wiki-pulse-critical');
    await expect(critical).toContainText('Critical pages');
    await expect(page.getByTestId('project-wiki-pulse-critical-open-engineering-workstream/30-system-knowledge/relocation.md')).toBeVisible();
    await expect(page.getByTestId('project-wiki-pulse-critical-open-scratch-idea.md')).toBeVisible();

    // No horizontal overflow with the new sections.
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    expect(overflow).toBe(false);

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'wiki-grading-trigger-and-critical--mocked.png'), fullPage: true });
  });
});
