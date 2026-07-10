import { test, expect, type Page } from '@playwright/test';
import * as path from 'path';

/**
 * Escalation summary panel (AGT-2019).
 *
 * A `5e-escalated` card — or an escalate-verdict card parked in 5-human-review,
 * the AGT-1994 case where the work was already merged — used to show only the
 * thin status protocol of the LAST run, hiding WHY it escalated and what was
 * delivered. The detail view now mounts a prominent escalation summary bundling:
 *   1. the open gate points (from the reissue follow-up checklist here),
 *   2. the code-review grade + verdict + summary,
 *   3. the delivery context (already in develop / main, commits + files),
 *   4. the gate recommendation (Needs decision).
 *
 * Fully mocked so it is deterministic and needs no live backend.
 */

const PROJECT = 'fixture-escalation';
const WATCH_PATH = 'C:/fixtures/escalation';
const JOB_ID = 'AGT-1994-fixture';

const SHOTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.join(process.env.JOB_RESULTS_DIR, '.')
  : path.resolve(__dirname, '../../test-results/escalation-summary');

const INFO = {
  id: JOB_ID,
  taskKey: `${WATCH_PATH}::${JOB_ID}`,
  title: 'Result-Templates Teil 2: JSON-Meta-Haelfte',
  // The AGT-1994 shape: escalate verdict, parked in 5-human-review, fully merged.
  state: '5-human-review',
  orchestratorVerdict: 'escalate',
  agent: 'claude',
  cliType: 'claude',
  model: 'claude-opus-4-8',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/.orchestrator/jobs/5e-escalated/${JOB_ID}`,
  execution: null,
  kind: 'task',
  epicId: null,
  commit: null,
  commits: [
    { sha: 'b2ed3f47', shortSha: 'b2ed3f47', message: 'first slice', filesChanged: 3, files: ['a.ts', 'b.ts', 'c.ts'], at: '2026-07-09T18:00:00Z' },
    { sha: '1a526e97', shortSha: '1a526e97', message: 'wire it', filesChanged: 2, files: ['c.ts', 'd.ts'], at: '2026-07-09T19:00:00Z' },
  ],
  mergeSignal: {
    branch: 'task/AGT-1994',
    inIntegration: true,
    inRelease: true,
    integrationBranch: 'develop',
    releaseBranch: 'main',
    integrationSha: 'b2ed3f4',
    releaseSha: '1a526e9',
  },
  tags: [],
  ownerClientId: 'local-default',
  lastUsage: null,
};

const DETAIL = {
  info: INFO,
  promptMarkdown: '# Task',
  promptHistory: [],
  titleHistory: [],
  statusMarkdown: '# Status\nResult: escalated\n\n- test bullet one\n- test bullet two\n- test bullet three',
  statusGeneration: null,
  contextUsage: null,
  log: [],
  summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null, bytesWritten: 10 },
  reviewEvidence: [],
};

const FOLLOW_UP = [
  '# Orchestrator follow-up',
  '',
  'STEER THE DIFF, DO NOT RESTART: close out only the open items.',
  '',
  '- [ ] Frontend build/unit/Playwright verification skipped (worktree limitation).',
  '- [ ] Live Haiku probe not run (dev backend offline).',
  '- [ ] Structured JSON aspect artefacts left for follow-up.',
].join('\n');

const CODE_REVIEW_LIST = {
  entries: [
    {
      fileName: 'code-review-grade-2026-07-09T19-22-02Z.md',
      verdict: 'pass',
      grade: 'B',
      summary: 'High-quality, wired, well-tested first slice that defers the JSON half and two metrics.',
      model: 'claude-opus-4-8',
      cliType: 'claude',
      runAt: '2026-07-09T19:22:02Z',
    },
  ],
};

const TIMELINE = [
  {
    ts: '2026-07-09T19:45:00Z',
    kind: 'orchestrator_escalated',
    actor: 'orchestrator',
    summary: 'escalated',
    details: {
      reason: 'Completion gate found unfinished work in the previous run.',
      cause: 'completion-gate',
      attempt: '3',
      maxAttempts: '3',
    },
  },
];

async function installRoutes(page: Page): Promise<void> {
  // Catch-all first (lowest priority); specific routes registered later win.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  // Shell boot dependencies.
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-09T20:00:00Z', snapshots: [] }) }));
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
        humanReview: [INFO], escalated: [], completed: [], archive: [],
      }),
    }));

  // Detail (broad) — must be registered before the narrower sub-routes below.
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(DETAIL) }));

  // The Overview tab polls `/api/tasks/{id}/pipeline` and reads `res.pipeline.allSteps`.
  // The catch-all above answers `[]`, whose `.pipeline` is undefined, so the component
  // throws and the shell raises a global "Unexpected application error" overlay that
  // floats over — and corrupts — the focused panel screenshot. Answer the real shape
  // (`null` = no pipeline yet) so the Overview tab renders cleanly and no overlay appears.
  await page.route(/\/api\/tasks\/[^/]+\/pipeline(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));

  // Narrow sub-routes win over the detail route (registered later).
  await page.route('**/code-review/list**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(CODE_REVIEW_LIST) }));
  await page.route('**/files/orchestrator-follow-up.md**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/plain', body: FOLLOW_UP }));
  await page.route('**/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(TIMELINE) }));
}

/**
 * The shell can surface a transient "Unexpected application error" overlay when an
 * un-mocked (or shape-mismatched) shell endpoint answers unexpectedly — the header
 * CLI-quota poll and the pipeline poll are the usual culprits. It renders above the
 * page and would bleed into a focused panel screenshot, so dismiss any such overlay
 * (Escape closes it) before capturing. Idempotent: a no-op when no dialog is present.
 */
async function dismissAppErrorDialog(page: Page): Promise<void> {
  const dialog = page.getByTestId('error-dialog');
  for (let i = 0; i < 3 && (await dialog.isVisible().catch(() => false)); i++) {
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function openDetail(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 960 });
  await installRoutes(page);
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await expect(page.getByTestId('escalation-summary')).toBeVisible({ timeout: 20_000 });
}

test.describe('Escalation summary panel', () => {
  test.beforeEach(() => test.setTimeout(60_000));

  test('bundles gate points, review verdict, delivery context and recommendation', async ({ page }, testInfo) => {
    await openDetail(page);

    // 1. Open gate points from the follow-up checklist.
    const gateItems = page.locator('[data-testid="escalation-gate-items"] li');
    await expect(gateItems).toHaveCount(3);
    await expect(page.getByTestId('escalation-gate-source')).toContainText('follow-up checklist');
    await expect(page.getByTestId('escalation-gate-count')).toContainText('3 open');

    // 2. Review verdict head.
    await expect(page.getByTestId('escalation-review-grade')).toHaveText('B');
    await expect(page.getByTestId('escalation-review-verdict')).toHaveText('pass');
    await expect(page.getByTestId('escalation-review-summary')).toContainText('first slice');

    // 3. Delivery context: merged into develop AND main, deduped file count.
    await expect(page.getByTestId('escalation-delivery-counts')).toContainText('2 commits');
    await expect(page.getByTestId('escalation-delivery-counts')).toContainText('4 files');

    // 4. Recommendation line.
    await expect(page.getByTestId('escalation-recommendation')).toHaveText('Needs decision');

    // Escalation reason headline from the timeline event.
    await expect(page.getByTestId('escalation-reason')).toContainText('Completion gate');

    // No stray shell-error overlay may be floating over the panel before we capture.
    await dismissAppErrorDialog(page);
    await expect(page.getByTestId('error-dialog')).toBeHidden();

    const panel = page.getByTestId('escalation-summary');
    for (const theme of ['dark', 'light'] as const) {
      await setTheme(page, theme);
      await page.waitForTimeout(200);
      await dismissAppErrorDialog(page);
      await panel.scrollIntoViewIfNeeded();
      const shot = await panel.screenshot();
      await testInfo.attach(`escalation-summary-${theme}--mocked.png`, { body: shot, contentType: 'image/png' });
      // Focused element shot for durable evidence under results/.
      await panel.screenshot({ path: path.join(SHOTS_DIR, `escalation-summary-${theme}--mocked.png`) }).catch(() => undefined);
    }
  });
});
