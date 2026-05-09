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

// Use a real project name so the frontend's active-project filter doesn't
// hide synthetic cards. The fixture data is still synthetic (controlled
// by makeJob); we just borrow the project label/path the frontend has
// already accepted from /api/watch-paths.
const PROJECT_NAME = 'Agent Software Studio';
const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

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
    }] : [],
    commitCount: hasCommit ? 1 : 0,
    sessionChain: [],
    fixture: false,
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
  // Stress distribution biased toward lanes that use the full job-card
  // render (which is what the user wants to scale). Archive uses a
  // different lightweight "archive-row" template, so testing scale there
  // measures the wrong thing; the perf concern is the busy lanes
  // (Completed, Human Review, Auto Review, Ready) that hold rich
  // job-cards with all the badges.
  const jobs: ReturnType<typeof makeJob>[] = [];
  const lanes: { lane: string; share: number }[] = [
    { lane: '6-completed',     share: 0.55 },
    { lane: '5-human-review',  share: 0.20 },
    { lane: '4-auto-review',   share: 0.15 },
    { lane: '2-ready',         share: 0.07 },
    { lane: '3-progress',      share: 0.02 },
    { lane: '7-archive',       share: 0.01 }, // a few for variety
  ];
  let i = 0;
  for (const { lane, share } of lanes) {
    const count = Math.max(0, Math.round(N * share));
    for (let k = 0; k < count && i < N; k++, i++) jobs.push(makeJob(i, lane));
  }
  while (i < N) { jobs.push(makeJob(i, '6-completed')); i++; }

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

  // Board endpoints - the load-bearing intercepts. The frontend
  // bootstraps from /api/jobs (flat) and /api/jobs/grouped (lane
  // buckets); both must return our synthetic shape. Other endpoints
  // (watch-paths, tags, clients, environment, runner status, snapshot)
  // fall through to the dev backend and use real defaults so the page
  // boots cleanly.
  await page.route(/\/api\/jobs\/grouped/, async route => route.fulfill({ json: grouped }));
  await page.route(/\/api\/jobs(\?|$)/, async route => route.fulfill({ json: jobs }));

  // All other endpoints (watch-paths, tags, clients, environment,
  // runner status, project snapshot, etc.) fall through to the real dev
  // backend, which serves them fast post-Cycle-5. We only intercept the
  // board-shape endpoints above so we can inject N synthetic cards.
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

      // Click-to-visible deliberately skipped: synthetic jobs don't have
      // a real backend, so the detail GET 404s and the panel never
      // opens. The render metrics above (initial-render, longtask,
      // scroll-fps, dom/heap) capture what matters for the "blazing
      // fast at 500 cards" question; click latency at scale needs a
      // real-data scenario, which the perf-baseline.spec.ts already
      // covers against the live workspace.

      console.log(`[N=${N}] initial=${initialRenderMs}ms cards=${cardCount}/${N} dom=${domCount} ` +
        `longtask=${longTotal.toFixed(0)}ms fps=${fps.toFixed(1)}`);
    });
  }
});
