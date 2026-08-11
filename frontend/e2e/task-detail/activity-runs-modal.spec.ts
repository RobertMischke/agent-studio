import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'path';
import { setTheme } from '../helpers/theme';

/**
 * Activity-pane slim-down: the RUNS bar moved out of the activity log.
 *
 * The activity-panel header now carries a compact `N Runs` chip plus a
 * small info `(i)` button. Clicking the chip opens a modal that holds the
 * full run list / run picker (the run-timeline that used to sit inline
 * below the plan strip). The info button explains what a "run" is via an
 * instant tooltip.
 *
 * These scenarios run against fully-mocked API routes so there is no
 * dependency on a running backend or a real git repository.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/activity-runs';
const JOB_ID = 'activity-runs-test';

interface RunFixture {
  index: number;
  intent: string;
  status: string;
  lineStart: number | null;
  lineEnd: number | null;
}

function makeRun(f: RunFixture) {
  const sha = `${f.index}`.padStart(40, `${f.index}`).slice(0, 40);
  return {
    index: f.index,
    intent: f.intent,
    startedAt: '2026-05-29T10:00:00Z',
    endedAt: '2026-05-29T10:05:00Z',
    status: f.status,
    cli: 'claude',
    exitCode: f.status === 'failed' ? 1 : 0,
    durationSeconds: 300,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: false,
    reason: null,
    userFollowup: f.index === 1 ? 'initial prompt' : 'please continue',
    lineStart: f.lineStart,
    lineEnd: f.lineEnd,
    headShaBefore: sha,
    headShaAfter: sha,
  };
}

const RUNS = [
  makeRun({ index: 1, intent: 'start', status: 'completed', lineStart: 1, lineEnd: 40 }),
  makeRun({ index: 2, intent: 'continue', status: 'failed', lineStart: 41, lineEnd: 80 }),
];

const VISUAL_RUNS = Array.from({ length: 10 }, (_, index) => makeRun({
  index: index + 1,
  intent: index === 0 ? 'start' : 'continue',
  status: index % 4 === 3 ? 'failed' : 'completed',
  lineStart: index * 40 + 1,
  lineEnd: (index + 1) * 40,
}));

const REVIEW_EVIDENCE = Array.from({ length: 10 }, (_, index) => ({
  id: `overflow-finding-${index + 1}`,
  source: index % 2 === 0 ? 'task-check' : 'security-audit',
  severity: index % 3 === 0 ? 'high' : 'info',
  title: `Responsive evidence finding ${index + 1}`,
  body: 'The detail column keeps this complete finding readable without introducing another scroll surface.',
  createdAt: `2026-05-29T10:${String(index).padStart(2, '0')}:00Z`,
  runIndex: index + 1,
  artifacts: [],
  fileRefs: [
    `frontend/src/app/features/task-detail/components/very-long-responsive-fixture-${index + 1}.component.scss:142`,
  ],
  acknowledged: false,
  followupJobId: null,
}));

function makeDetail(state: string) {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Activity runs fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: REVIEW_EVIDENCE,
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page, runs: object[]): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail('3-progress');

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }),
  );
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        autoReview: [],
        humanReview: [],
        completed: [],
        archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
      ]),
    }),
  );
  await page.route('**/api/workspaces**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/projects**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/projects/*/workbenches**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
    }),
  );
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    }),
  );
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-29T00:00:00Z', snapshots: [] }),
    }),
  );
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/pipeline(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        pipeline: { pre: [], core: [], post: [], allSteps: [] },
        execution: null,
        executions: [],
        config: {},
        cost: null,
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs/\\d+/commits(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runIndex: 1,
        startedAt: '2026-05-29T10:00:00Z',
        endedAt: '2026-05-29T10:05:00Z',
        headShaBefore: null,
        headShaAfter: null,
        source: 'wall-clock',
        commits: [],
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        runCount: runs.length,
        firstStartedAt: '2026-05-29T10:00:00Z',
        lastActivityAt: '2026-05-29T10:05:00Z',
        hasActiveRun: false,
        runs,
        reviewAttemptEpoch: 1,
        reviewAttemptCycles: [
          {
            epoch: 1,
            isCurrent: true,
            startedAt: '2026-05-29T09:45:00Z',
            endedAt: null,
            actor: 'human:operator@example.com',
            reason: 'Runner repaired; assess the card from fresh evidence.',
            fromState: '5e-escalated',
            toState: '4-auto-review',
            rotatedArtifacts: 3,
          },
          {
            epoch: 0,
            isCurrent: false,
            startedAt: '2026-05-28T20:00:00Z',
            endedAt: '2026-05-29T09:45:00Z',
            actor: null,
            reason: 'Initial review cycle.',
            fromState: null,
            toState: null,
            rotatedArtifacts: 0,
          },
        ],
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isRepo: true,
        branch: 'main',
        filesChanged: 0,
        totalAdded: 0,
        totalRemoved: 0,
        files: [],
        error: null,
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const VISUAL_PHASE = process.env.TASK_DETAIL_OVERFLOW_VISUAL_PHASE ?? 'after';

async function dismissErrorDialog(page: Page): Promise<void> {
  await page.evaluate(() => document.querySelector('vite-error-overlay')?.remove());
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => undefined);
  }
}

async function openDetail(page: Page): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await dismissErrorDialog(page);
  await expect(page.getByTestId('activity-runs')).toBeVisible({ timeout: 10_000 });
}

test.describe('Activity-pane: compact N Runs chip + modal', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: false, protocol: true, git: false }),
        );
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
      } catch {
        return;
      }
    });
  });

  test('header shows a compact "N Runs" chip and an info button', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    const chip = page.getByTestId('activity-runs-open');
    await expect(chip).toBeVisible();
    await expect(chip).toHaveText('2 Runs');
    await expect(page.getByTestId('activity-runs-info')).toBeVisible();

    // The inline run-timeline must NOT be on the page until the modal opens.
    await expect(page.getByTestId('runs-modal')).toHaveCount(0);
    await expect(page.getByTestId('runs-icon-row')).toHaveCount(0);

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-chip.png') });
    }
  });

  test('chip uses the singular "1 Run" for a single run', async ({ page }) => {
    await installRoutes(page, [RUNS[0]]);
    await openDetail(page);
    await expect(page.getByTestId('activity-runs-open')).toHaveText('1 Run');
  });

  test('clicking the chip opens the run-list modal with the full picker', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();

    const modal = page.getByTestId('runs-modal');
    await expect(modal).toBeVisible();
    await expect(page.getByTestId('runs-icon-row')).toBeVisible();
    await expect(page.getByTestId('run-icon-1')).toBeVisible();
    await expect(page.getByTestId('run-icon-2')).toBeVisible();
    await expect(page.getByTestId('runs-aggregate')).toContainText('2 runs');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-modal-open.png') });
    }
  });

  test('the modal shows the current review epoch and closed cycle history', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();

    const history = page.getByTestId('review-attempt-history');
    await expect(history).toBeVisible();
    await expect(page.getByTestId('review-attempt-current')).toContainText('Epoch 1');
    await expect(page.getByTestId('review-attempt-cycle-1')).toContainText(
      'Runner repaired; assess the card from fresh evidence.',
    );
    await expect(page.getByTestId('review-attempt-cycle-1')).toContainText('3 artifacts archived');
    await expect(page.getByTestId('review-attempt-cycle-0')).toContainText('Initial review cycle.');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-review-epochs.png') });
    }
  });

  test('review epoch history remains legible in dark theme', async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('atp.studio.theme', 'dark');
      } catch {
        return;
      }
    });
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();

    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
    await expect(page.getByTestId('review-attempt-cycle-1')).toBeVisible();
    await expect(page.getByTestId('review-attempt-cycle-0')).toContainText('Closed');

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'activity-runs-review-epochs-dark.png') });
    }
  });

  test('the close button, backdrop, and Escape all dismiss the modal', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    const open = page.getByTestId('activity-runs-open');
    const modal = page.getByTestId('runs-modal');

    // Close button.
    await open.click();
    await expect(modal).toBeVisible();
    await page.getByTestId('runs-modal-close').click();
    await expect(modal).toHaveCount(0);

    // Backdrop click (the backdrop is the test-id element; clicking it at a
    // corner hits the backdrop, not the centered dialog).
    await open.click();
    await expect(modal).toBeVisible();
    await modal.click({ position: { x: 5, y: 5 } });
    await expect(modal).toHaveCount(0);

    // Escape key.
    await open.click();
    await expect(modal).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0);
  });

  test('run selection still works inside the modal (no regression)', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();
    await expect(page.getByTestId('runs-modal')).toBeVisible();

    // Clicking a run icon expands that run's popover card — the run-picker
    // behavior that used to live inline still works in the modal.
    await page.getByTestId('run-icon-1').click();
    await expect(page.getByTestId('run-popover-1')).toBeVisible();
  });

  test('task-detail tabs and run panel visual matrix', async ({ page }) => {
    test.setTimeout(90_000);
    await page.addInitScript(() => {
      const weights = localStorage.getItem('agt-2625-visual-pane-weights');
      localStorage.setItem(
        'taskboard.panesVisible',
        JSON.stringify({ prompt: true, protocol: true, git: false }),
      );
      if (weights) localStorage.setItem('taskboard.paneWeights', weights);
      localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
    });
    await installRoutes(page, VISUAL_RUNS);

    const layouts = [
      { name: 'narrow', width: 900, height: 720, weights: { prompt: 3, protocol: 5, git: 4 } },
      { name: 'wide', width: 1600, height: 900, weights: { prompt: 4, protocol: 4, git: 4 } },
    ] as const;

    for (const layout of layouts) {
      await page.setViewportSize({ width: layout.width, height: layout.height });
      await page.addInitScript((weights) => {
        localStorage.setItem('agt-2625-visual-pane-weights', JSON.stringify(weights));
        localStorage.setItem('taskboard.paneWeights', JSON.stringify(weights));
      }, layout.weights);
      await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      await dismissErrorDialog(page);

      await expect(page.getByTestId('pane-prompt-header')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('activity-runs-open')).toBeVisible();
      await dismissErrorDialog(page);

      if (VISUAL_PHASE === 'after') {
        const promptHeader = page.getByTestId('pane-prompt-header');
        const tabsGeometry = await promptHeader.evaluate((header) => {
          const tabs = header.querySelector<HTMLElement>('app-pane-tabs');
          const firstControl = header.querySelector<HTMLElement>('[data-testid="pane-header-maximize"]');
          if (!tabs || !firstControl) throw new Error('Prompt header geometry is incomplete');
          const tabsRect = tabs.getBoundingClientRect();
          const controlRect = firstControl.getBoundingClientRect();
          return {
            headerOverflow: header.scrollWidth - header.clientWidth,
            gap: controlRect.left - tabsRect.right,
          };
        });
        expect(tabsGeometry.headerOverflow).toBeLessThanOrEqual(1);
        expect(tabsGeometry.gap).toBeGreaterThanOrEqual(4);

        const overviewLabel = promptHeader
          .getByTestId('prompt-tab-overview')
          .locator('.pane-tab__label');
        await expect(overviewLabel).toHaveCSS('text-overflow', 'ellipsis');
        if (layout.name === 'narrow') {
          await expect(promptHeader.getByTestId('pane-tabs-overflow')).toBeVisible();
        } else {
          await expect(promptHeader.getByTestId('pane-tabs-overflow')).toHaveCount(0);
          await expect(promptHeader.getByTestId('prompt-tab-evidence')).toBeVisible();
        }

        const promptBody = page.locator('[data-testid="pane-prompt"] > .pane__body');
        const overviewOverflow = await promptBody.evaluate(body => body.scrollWidth - body.clientWidth);
        expect(overviewOverflow).toBeLessThanOrEqual(1);

        const evidenceTab = promptHeader.getByTestId('prompt-tab-evidence');
        if (await evidenceTab.isVisible().catch(() => false)) {
          await evidenceTab.click();
        } else {
          await promptHeader.getByTestId('pane-tabs-overflow').click();
          await page.getByTestId('pane-tabs-overflow-item-evidence').click();
        }
        await expect(page.getByTestId('review-evidence-panel')).toBeVisible();
        const evidenceGeometry = await promptBody.evaluate((body) => {
          const scrollables = [body, ...Array.from(body.querySelectorAll<HTMLElement>('*'))]
            .filter((element) => {
              const overflowY = getComputedStyle(element).overflowY;
              return /^(auto|scroll)$/.test(overflowY) && element.scrollHeight > element.clientHeight + 1;
            })
            .map(element => element.className);
          return {
            horizontalOverflow: body.scrollWidth - body.clientWidth,
            scrollables,
          };
        });
        expect(evidenceGeometry.horizontalOverflow).toBeLessThanOrEqual(1);
        expect(evidenceGeometry.scrollables).toEqual(['pane__body']);

        const overviewTab = promptHeader.getByTestId('prompt-tab-overview');
        if (await overviewTab.isVisible().catch(() => false)) {
          await overviewTab.click();
        } else {
          await promptHeader.getByTestId('pane-tabs-overflow').click();
          await page.getByTestId('pane-tabs-overflow-item-overview').click();
        }
        await expect(page.getByTestId('overview-tab')).toBeVisible();
      }

      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        if (RESULTS_DIR) {
          mkdirSync(RESULTS_DIR, { recursive: true });
          await page.screenshot({
            path: path.join(
              RESULTS_DIR,
              `task-detail-tabs-${VISUAL_PHASE}-${layout.name}-${theme}--mocked.png`,
            ),
            fullPage: false,
          });
        }
      }

      await dismissErrorDialog(page);
      await page
        .getByTestId('pane-prompt-header')
        .getByTestId('pane-header-hide')
        .click();
      await expect(page.getByTestId('pane-prompt-header')).toHaveCount(0);
      await dismissErrorDialog(page);
      await page.getByTestId('activity-runs-open').click();
      await dismissErrorDialog(page);
      if (await page.getByTestId('runs-modal').count() === 0) {
        await page.getByTestId('activity-runs-open').click();
      }
      await expect(page.getByTestId('runs-modal')).toBeVisible();
      await page.getByTestId('run-icon-1').click();
      await expect(page.getByTestId('run-popover-1')).toBeVisible();

      if (VISUAL_PHASE === 'after') {
        const runsBody = page.getByTestId('runs-modal').locator('.runs-modal__body');
        const runsGeometry = await runsBody.evaluate((body) => {
          const scrollables = [body, ...Array.from(body.querySelectorAll<HTMLElement>('*'))]
            .filter((element) => {
              const overflowY = getComputedStyle(element).overflowY;
              return /^(auto|scroll)$/.test(overflowY) && element.scrollHeight > element.clientHeight + 1;
            })
            .map(element => element.className);
          const cards = Array.from(body.querySelectorAll<HTMLElement>('[data-testid^="run-popover-"]'));
          return {
            horizontalOverflow: body.scrollWidth - body.clientWidth,
            maxCardOverflow: cards.reduce(
              (maximum, card) => Math.max(maximum, card.scrollWidth - card.clientWidth),
              0,
            ),
            overflowX: getComputedStyle(body).overflowX,
            scrollables,
          };
        });
        expect(runsGeometry.horizontalOverflow).toBeLessThanOrEqual(1);
        expect(runsGeometry.maxCardOverflow).toBeLessThanOrEqual(1);
        expect(runsGeometry.overflowX).toBe('hidden');
        expect(runsGeometry.scrollables).toEqual(['runs-modal__body']);
      }

      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        if (RESULTS_DIR) {
          mkdirSync(RESULTS_DIR, { recursive: true });
          await page.screenshot({
            path: path.join(
              RESULTS_DIR,
              `task-detail-runs-${VISUAL_PHASE}-${layout.name}-${theme}--mocked.png`,
            ),
            fullPage: false,
          });
        }
      }

      await page.getByTestId('runs-modal-close').click();
      await expect(page.getByTestId('runs-modal')).toHaveCount(0);
    }
  });

  test('filtering the log to a run closes the modal', async ({ page }) => {
    await installRoutes(page, RUNS);
    await openDetail(page);

    await page.getByTestId('activity-runs-open').click();
    await page.getByTestId('run-icon-1').click();
    await expect(page.getByTestId('run-popover-1')).toBeVisible();

    await page
      .getByRole('button', { name: 'Filter activity log to this run' })
      .first()
      .click();

    // onRunFilter applies the range and closes the modal so the user can
    // see the filtered log underneath.
    await expect(page.getByTestId('runs-modal')).toHaveCount(0);
  });
});
