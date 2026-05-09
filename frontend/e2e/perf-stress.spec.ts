import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { startLongTaskRecorder } from './helpers/timing';

/**
 * Cycle 7 stress measurement. Decouples the frontend's render cost from
 * the backend by intercepting the polled HTTP endpoints with `page.route`
 * and serving N synthetic jobs - 10 / 100 / 200 / 500 - so we can find
 * the scaling cliffs without polluting the real workspace.
 *
 * Three layers of metric per N:
 *  1. Render-arrival - time from page load to the first kanban card
 *     becoming visible (DOM + change detection settled).
 *  2. Steady-state cost - DOM node count, JS heap (via CDP), longtask
 *     budget over a 5 s idle window. These show what the browser pays
 *     just to KEEP the board on screen.
 *  3. Interaction smoothness - rAF-based FPS during a programmatic
 *     scroll, click-to-visible for opening a card detail.
 *
 * Gated by RUN_PERF_BASELINE=1; scenario tag picks up from
 * PERF_SCENARIO env var (default `stress`). Each test appends one
 * JSONL row per measurement to logs/perf/stress-<scenario>-<tag>.jsonl
 * for the report generator to consume.
 */

const SCENARIO = process.env.PERF_SCENARIO || 'stress';
const REPO_ROOT = path.resolve(process.cwd(), '..');
const PERF_DIR = path.join(REPO_ROOT, 'logs', 'perf');
const RUN_TAG = process.env.PERF_RUN_TAG || new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
const JSONL_PATH = path.join(PERF_DIR, `stress-${SCENARIO}-${RUN_TAG}.jsonl`);

interface Sample {
  scenario: string;
  N: number;
  metric: string;
  unit: 'ms' | 'count' | 'bytes' | 'fps';
  value: number;
  notes?: string;
}

function record(N: number, metric: string, unit: Sample['unit'], value: number, notes?: string) {
  fs.mkdirSync(PERF_DIR, { recursive: true });
  const row: Sample = { scenario: SCENARIO, N, metric, unit, value, notes };
  fs.appendFileSync(JSONL_PATH, JSON.stringify(row) + '\n');
}

const PROJECT_NAME = 'Stress Test';
const WATCH_PATH = '/stress/test';

/**
 * Build a single synthetic JobInfo. Fields cover what JobCard actually
 * reads (id, title, state, agent, cliType, lastActivity, createdAt,
 * commit, taskType); optional badges (tokenSummary, autoLoop, summary,
 * pendingIntent) are left null so the card renders its compact state.
 * Sprinkles a few jobs with a fake commit so the git chip exercises its
 * render path, and a few tags so the tag chips run.
 */
function makeJob(i: number, lane: string) {
  const id = `stress-job-${i.toString().padStart(5, '0')}`;
  const hasCommit = i % 7 === 0;
  const hasTokens = i % 4 === 0;
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `Stress Test Job ${i} - lorem ipsum dolor sit amet`,
    state: lane,
    order: i,
    agent: 'claude',
    createdAt: new Date(Date.now() - i * 60_000).toISOString(),
    watchPath: WATCH_PATH,
    projectName: PROJECT_NAME,
    folderPath: `${WATCH_PATH}/${lane}/${id}`,
    lastActivity: new Date(Date.now() - i * 30_000).toISOString(),
    sessionName: null,
    model: 'claude-sonnet-4-6',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: hasCommit ? {
      sha: `${i.toString(16).padStart(40, '0')}`,
      shortSha: i.toString(16).padStart(7, '0').slice(0, 7),
      message: `feat(stress): commit for job ${i}`,
      filesChanged: 1 + (i % 5),
      at: new Date(Date.now() - i * 30_000).toISOString(),
      files: [],
    } : null,
    commits: hasCommit ? [{
      sha: `${i.toString(16).padStart(40, '0')}`,
      shortSha: i.toString(16).padStart(7, '0').slice(0, 7),
      message: `feat(stress): commit for job ${i}`,
      filesChanged: 1 + (i % 5),
      at: new Date(Date.now() - i * 30_000).toISOString(),
      files: [],
    }] : undefined,
    tokenSummary: hasTokens ? {
      totalTokens: 1000 * i,
      inputTokens: 500 * i,
      outputTokens: 500 * i,
      cacheReadTokens: 0,
      cacheCreationTokens: 0,
      callCount: 1 + (i % 3),
      runCount: 1,
    } : null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    orchestratorVerdict: null,
    ownerClientId: 'local-default',
    phase: null,
    taskType: i % 3 === 0 ? 'bug' : i % 3 === 1 ? 'feature' : 'chore',
    tags: i % 5 === 0 ? ['stress', 'fixture'] : [],
  };
}

function makeFixture(N: number) {
  // Realistic distribution: bulk in archive (the "long lane" case),
  // a few in 2-ready / 3-progress / 4-auto-review / 6-completed for variety.
  // Mirrors the lane mix from the real workspace at 358 jobs.
  const jobs: ReturnType<typeof makeJob>[] = [];
  const lanes: { lane: string; share: number }[] = [
    { lane: '7-archive', share: 0.78 },
    { lane: '6-completed', share: 0.10 },
    { lane: '4-auto-review', share: 0.06 },
    { lane: '5-human-review', share: 0.03 },
    { lane: '2-ready', share: 0.02 },
    { lane: '3-progress', share: 0.01 },
  ];
  let i = 0;
  for (const { lane, share } of lanes) {
    const count = Math.max(0, Math.round(N * share));
    for (let k = 0; k < count && i < N; k++, i++) jobs.push(makeJob(i, lane));
  }
  while (i < N) { jobs.push(makeJob(i, '7-archive')); i++; }

  const grouped = {
    backlog: jobs.filter(j => j.state === '0-backlog'),
    preparation: jobs.filter(j => j.state === '1-preparation'),
    orchestratorPrep: [],
    needsHumanReview: [],
    ready: jobs.filter(j => j.state === '2-ready'),
    progress: jobs.filter(j => j.state === '3-progress'),
    failedPickup: [],
    autoReview: jobs.filter(j => j.state === '4-auto-review'),
    humanReview: jobs.filter(j => j.state === '5-human-review'),
    review: jobs.filter(j => j.state === '4-auto-review'), // legacy alias
    completed: jobs.filter(j => j.state === '6-completed'),
    archive: jobs.filter(j => j.state === '7-archive'),
  };
  return { jobs, grouped };
}

async function installRoutes(page: import('@playwright/test').Page, N: number) {
  const { jobs, grouped } = makeFixture(N);

  // Debug: log every intercepted URL so we can see what routes actually fire.
  const seen = new Set<string>();
  page.on('request', req => {
    const u = req.url();
    if (u.includes('/api/') && !seen.has(u)) { seen.add(u); console.log('REQ', u); }
  });

  // Board endpoints - the load-bearing intercepts. Registered LAST so
  // they win over the catch-all (Playwright handlers are LIFO).
  await page.route(/\/api\/jobs\/grouped/, async route => route.fulfill({ json: grouped }));
  await page.route(/\/api\/jobs(\?|$)/, async route => route.fulfill({ json: jobs }));

  // Watch paths - frontend bootstraps from this on app init.
  await page.route('**/api/watch-paths', async route => route.fulfill({ json: [
    { name: PROJECT_NAME, path: WATCH_PATH, rootPath: '/stress', repositoryPath: '' }
  ]}));

  // Runner status - empty active state so the per-project pollers don't fire.
  await page.route('**/api/runner/status', async route => route.fulfill({ json: {
    projects: { [PROJECT_NAME]: { projectName: PROJECT_NAME, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } }
  }}));

  // Chatty per-project endpoints - empty responses so we don't measure them.
  await page.route('**/api/projects/**/snapshot', async route => route.fulfill({ json: {
    project: PROJECT_NAME, capturedAt: new Date().toISOString(),
    settings: { autoCommit: false, runnerMode: 'manual', orchestratorModel: null },
    runnerStatus: { projectName: PROJECT_NAME, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
    orchestratorLogTail: [], orchestratorSession: null,
    reviewDecisionsPending: [], runnerPendingDecisions: []
  }}));
  await page.route('**/api/projects/**/review-decisions-pending', async route => route.fulfill({ json: { project: PROJECT_NAME, items: [] } }));
  await page.route('**/api/runner/**/pending-decisions', async route => route.fulfill({ json: { project: PROJECT_NAME, items: [] } }));
  await page.route('**/api/runner/**/orchestrator-log', async route => route.fulfill({ json: { project: PROJECT_NAME, entries: [] } }));
  await page.route('**/api/runner/**/orchestrator-session', async route => route.fulfill({ json: { project: PROJECT_NAME, session: null } }));
  await page.route('**/api/projects/settings', async route => route.fulfill({ json: { [PROJECT_NAME]: { autoCommit: false, runnerMode: 'manual', orchestratorModel: null } } }));
  await page.route('**/api/projects/**/autonomy', async route => route.fulfill({ json: { level: 0 } }));

  // Quota / usage - the slow ones. Empty responses keep the spec under 60 s.
  await page.route('**/api/cli/quota', async route => route.fulfill({ json: { sections: [] } }));
  await page.route('**/api/cli/usage', async route => route.fulfill({ json: { sections: [] } }));
  await page.route('**/api/cli/*/models', async route => route.fulfill({ json: { models: [] } }));
  await page.route('**/api/auto-review/status', async route => route.fulfill({ json: { lastTickAt: null, accept: 0, reissue: 0, escalate: 0, aspectsRun: 0, currentJob: null, currentProject: null } }));
  await page.route('**/api/git/summary', async route => route.fulfill({ json: [] }));
  await page.route('**/api/git/hygiene*', async route => route.fulfill({ json: { project: PROJECT_NAME, dirty: false, unpushed: 0, branch: 'main' } }));
  await page.route('**/api/tags', async route => route.fulfill({ json: { tags: [] } }));
  await page.route('**/api/projects/**/token-summary', async route => route.fulfill({ json: { totalTokens: 0, totalCost: 0, jobCount: 0 } }));
  await page.route('**/api/runner/**/token-summary', async route => route.fulfill({ json: { totalTokens: 0, totalCost: 0, jobCount: 0 } }));
  await page.route('**/api/runner/global/orchestrator-session', async route => route.fulfill({ json: { project: '(global)', session: null } }));
}

test.describe('Frontend stress: render perf at scale', () => {
  test.beforeAll(() => {
    if (process.env.RUN_PERF_BASELINE !== '1') {
      test.skip(true, 'Set RUN_PERF_BASELINE=1 to capture stress data.');
    }
    if (process.env.PERF_RESET === '1' && fs.existsSync(JSONL_PATH)) {
      fs.unlinkSync(JSONL_PATH);
    }
  });

  for (const N of [10, 100, 200, 500]) {
    test(`N=${N}: initial render + steady state + scroll`, async ({ page, context }) => {
      await installRoutes(page, N);

      // 1. Render-arrival: time from goto to first card visible.
      const t0 = Date.now();
      await page.goto('/');
      // Don't wait for networkidle - polled endpoints will keep firing.
      // Wait for the first card to be visible: that's "the user can see
      // the board" by definition.
      await page.getByTestId('job-card').first().waitFor({ state: 'visible', timeout: 15_000 });
      const initialRenderMs = Date.now() - t0;
      record(N, 'initial-render-to-first-card', 'ms', initialRenderMs);

      // Settle so polling has fired at least once and DOM has stabilised.
      await page.waitForTimeout(1000);

      // 2a. DOM node count - cheap proxy for "how big is the rendered tree".
      const domCount = await page.evaluate(() => document.querySelectorAll('*').length);
      record(N, 'dom-node-count', 'count', domCount);

      // 2b. Visible card count - sanity check that the fixture actually
      //     reached the DOM (and to verify any virtualization).
      const cardCount = await page.getByTestId('job-card').count();
      record(N, 'rendered-card-count', 'count', cardCount,
        cardCount < N ? 'fewer cards than fixture - virtualization or hide-when-empty in effect' : 'all cards rendered');

      // 2c. JS heap via CDP. Best available browser-side memory signal.
      try {
        const cdp = await context.newCDPSession(page);
        await cdp.send('Performance.enable');
        const metrics = await cdp.send('Performance.getMetrics');
        const heap = metrics.metrics.find(m => m.name === 'JSHeapUsedSize')?.value || 0;
        const docs = metrics.metrics.find(m => m.name === 'Documents')?.value || 0;
        const nodes = metrics.metrics.find(m => m.name === 'Nodes')?.value || 0;
        record(N, 'js-heap-bytes', 'bytes', heap);
        record(N, 'cdp-document-count', 'count', docs);
        record(N, 'cdp-node-count', 'count', nodes);
      } catch (err) {
        // CDP only on chromium; soft-skip elsewhere.
      }

      // 2d. Long tasks during 5 s steady-state idle. The biggest signal
      //     for "the UI feels stuck while just sitting there".
      const recorder = await startLongTaskRecorder(page);
      await page.waitForTimeout(5_000);
      const longTotal = await recorder.totalMs();
      const longCount = await recorder.count();
      await recorder.stop();
      record(N, 'long-tasks-total-during-5s-idle', 'ms', longTotal,
        `${longCount} long task(s)`);

      // 3a. Scroll FPS - rAF-counted frames during a 2 s programmatic scroll.
      //     Anything below ~50 FPS is visible jank to the user.
      const fps = await page.evaluate(async () => {
        return await new Promise<number>(resolve => {
          // Find the main scroll container - body for the kanban board layout.
          const scroller = document.scrollingElement || document.documentElement;
          let frames = 0;
          const startMark = performance.now();
          let lastTime = startMark;
          let dir = 1;
          function tick() {
            frames++;
            const now = performance.now();
            const elapsed = now - startMark;
            if (elapsed > 2000) {
              resolve(frames / (elapsed / 1000));
              return;
            }
            // Drive scroll by 8 px each frame (alternating direction)
            // so the test exercises layout/paint without ever scrolling
            // off-screen.
            scroller.scrollBy({ top: 8 * dir });
            if (Math.floor(elapsed / 250) % 2 === 1) dir = -1; else dir = 1;
            requestAnimationFrame(tick);
          }
          requestAnimationFrame(tick);
        });
      });
      record(N, 'scroll-fps-2s', 'fps', fps);

      // 3b. Click-to-visible: click the first card, wait for detail-panes.
      //     Only run for N <= 200 to keep total spec under 5 min.
      if (N <= 200) {
        const cards = page.getByTestId('job-card');
        const target = page.getByTestId('detail-panes');
        const clickStart = Date.now();
        await cards.first().click();
        await target.waitFor({ state: 'visible', timeout: 8_000 });
        record(N, 'click-to-visible-detail', 'ms', Date.now() - clickStart);
      }

      console.log(`[N=${N}] initial=${initialRenderMs}ms cards=${cardCount}/${N} dom=${domCount} ` +
        `longtask=${longTotal.toFixed(0)}ms fps=${fps.toFixed(1)}`);
    });
  }
});
