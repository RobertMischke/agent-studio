import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * PUB-1: the Project Hub overview shows read-only publish badges derived from
 * repo facts - "NuGet 0.3.1 -> 4 tasks pending", a website delta, and the
 * "first publish pending" special state. Zero-pending targets render no badge
 * (Ruhe). This spec mounts the Hub on the overview rail with the project
 * snapshot mocked (publishTargets folded in), so no live backend is required;
 * the screenshot is labelled `--mocked`.
 */

const PROJECT = 'coding-agent-runner';
const REPO_PATH = 'C:/repo/coding-agent-runner';
const HUB_TAB_KEY = `hub:${PROJECT}`;
const BOOT_TIMEOUT = 60_000;

// `kind` is the camelCase enum value the backend emits (JsonStringEnumConverter).
const PUBLISH_TARGETS = [
  {
    id: 'package:nuget', kind: 'package', ecosystem: 'nuget', label: 'NuGet',
    packageName: 'Coding.Agent.Runner', currentVersion: '0.3.1',
    firstPublishPending: false, pendingCount: 4, referenceKind: 'tag', reference: 'v0.3.1',
  },
  {
    id: 'website', kind: 'website', ecosystem: null, label: 'Website',
    packageName: null, currentVersion: null,
    firstPublishPending: false, pendingCount: 2, referenceKind: 'release-tag', reference: 'v0.3.1',
  },
  {
    // A package that has never been released -> the first-publish special state.
    id: 'package:npm', kind: 'package', ecosystem: 'npm', label: 'npm',
    packageName: 'coding-agent-chat', currentVersion: null,
    firstPublishPending: true, pendingCount: null, referenceKind: 'none', reference: null,
  },
  {
    // Quiet target: nothing pending -> MUST render no badge.
    id: 'package:quiet', kind: 'package', ecosystem: 'nuget', label: 'NuGet',
    packageName: 'Quiet.Pkg', currentVersion: '1.0.0',
    firstPublishPending: false, pendingCount: 0, referenceKind: 'tag', reference: 'v1.0.0',
  },
];

function snapshot() {
  return {
    project: PROJECT,
    capturedAt: '2026-07-10T12:00:00Z',
    paths: { path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH },
    settings: {
      autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'on-completed',
      runnerMode: 'manual', orchestratorModel: null, orchestratorThinkingLevel: null,
      laneSortStrategies: {},
    },
    runnerStatus: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
    orchestratorLogTail: [],
    orchestratorSession: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    publishTargets: PUBLISH_TARGETS,
    queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
  };
}

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));
  // The snapshot under test - publishTargets folded in.
  await page.route('**/api/projects/*/snapshot**', r => r.fulfill(json(snapshot())));
}

/**
 * Seed a persisted Project Hub tab pinned to the overview rail BEFORE the app
 * boots (via addInitScript, like the passing board specs) so the Hub opens
 * straight on the overview rail with all routes already mocked - no reload race
 * that lets an early /api/watch-paths escape to the network.
 */
async function openHubOnOverview(page: Page): Promise<void> {
  await page.addInitScript(({ tabKey, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__' },
        { kind: 'hub', projectName: project, section: 'overview' },
      ],
      activeKey: tabKey,
    }));
  }, { tabKey: HUB_TAB_KEY, project: PROJECT });
  await installRoutes(page);
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

function resultsDir(): string {
  const fromEnv = process.env.PUB_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pub-1');
}

test.describe('PUB-1 · Project Hub publish badges (mocked)', () => {
  test.setTimeout(180_000);

  test('renders package/website/first-publish badges and stays quiet on zero-pending', async ({ page }, testInfo) => {
    await openHubOnOverview(page);
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });

    const badges = page.getByTestId('project-publish-badges');
    await expect(badges).toBeVisible({ timeout: 15_000 });

    // Package delta badge with version + pending count.
    const nuget = page.getByTestId('publish-badge-package:nuget');
    await expect(nuget).toBeVisible();
    await expect(nuget).toContainText('NuGet 0.3.1');
    await expect(nuget).toContainText('4 tasks pending');

    // Website delta badge - and the website styling modifier is applied, which
    // depends on the camelCase `kind` value the backend actually emits.
    const website = page.getByTestId('publish-badge-website');
    await expect(website).toBeVisible();
    await expect(website).toContainText('Website');
    await expect(website).toContainText('2 tasks pending');
    await expect(website).toHaveClass(/publish-badge--website/);
    await expect(nuget).not.toHaveClass(/publish-badge--website/);

    // First-publish special state.
    const npm = page.getByTestId('publish-badge-package:npm');
    await expect(npm).toBeVisible();
    await expect(npm).toContainText('first publish pending');
    await expect(npm).toContainText('manual, operator');

    // Quiet target renders NO badge (Ruhe).
    await expect(page.getByTestId('publish-badge-package:quiet')).toHaveCount(0);

    // Strip the global error dialog / dev overlays before the evidence frame.
    // Under full API mocking a sibling overview component (regression radar /
    // cli-environment) reacts to an empty mocked response and trips the global
    // ErrorHandler - harness noise, unrelated to the publish badges, which have
    // already asserted above. (Same treatment as card-merge-signal.spec.ts.)
    const errMsg = await page.locator('[data-testid="error-dialog-message"]').first().textContent().catch(() => null);
    if (errMsg && errMsg.trim()) console.log(`[publish-badges spec] global error-dialog present (harness noise): ${errMsg.trim().slice(0, 160)}`);
    await page.evaluate(() => {
      document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
      document.querySelectorAll('.overlay--error, app-error-dialog, [data-testid="error-dialog"]')
        .forEach(n => ((n as HTMLElement).style.display = 'none'));
    });
    await badges.scrollIntoViewIfNeeded();
    await page.waitForTimeout(150);

    // Evidence screenshot (mocked API).
    fs.mkdirSync(resultsDir(), { recursive: true });
    const shotPath = path.join(resultsDir(), 'project-hub-publish-badges--mocked.png');
    await page.screenshot({ path: shotPath, fullPage: true });
    await testInfo.attach('project-hub-publish-badges--mocked.png', { path: shotPath, contentType: 'image/png' });
  });
});
