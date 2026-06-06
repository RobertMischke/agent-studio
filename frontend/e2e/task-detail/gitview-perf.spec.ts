import { test, expect, Page } from '@playwright/test';

/**
 * GitView performance — Issue 2 diagnosis + regression guard.
 *
 * Operator report (2026-05-28): "Wenn ich in der Detailansicht von dem
 * GitView bin und dann inwaerts mache, habe ich enorme Performance-Issues."
 * i.e. drilling into files (clicking tree rows) felt slow.
 *
 * Diagnosis (see results/perf-diagnosis.md): every tree-row click paid a
 * fresh backend round-trip + a diff2html re-render, INCLUDING clicks back
 * to a file already viewed. The fix is a 32-entry client-side LRU diff
 * cache in GitPaneService keyed by `mode|sha|path`; a cache hit sets
 * `diffText` synchronously so the `diffHtml` computed re-renders in the
 * same tick with no network and no re-import of diff2html.
 *
 * This spec instruments that contract against the prompt's targets:
 *   - Expand a NOT-yet-viewed file  → exactly one diff fetch (network).
 *   - Re-open an already-viewed file → CACHE HIT: zero new fetches and
 *     time-to-content < 100 ms (prompt target for files < 5000 lines).
 *   - Hover sequence over rows       → zero fetches (hover is CSS-only,
 *     no JS-driven diff load), so no per-hover main-thread/network cost.
 *
 * The diff route carries a deliberate 150 ms latency so an uncached fetch
 * is *observably* slower than a cache hit — the measured gap is the
 * evidence that the cache removed the round-trip the operator was paying.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/gitview-perf';
const JOB_ID = 'gitview-perf-test';
const DIFF_LATENCY_MS = 150;

const COMMIT = {
  sha: 'cafef00d1234567890cafef00d1234567890cafe',
  shortSha: 'cafef00',
  message: 'perf fixture: two files so we can drill in and back out',
  filesChanged: 2,
  files: ['frontend/src/app/alpha.component.ts', 'frontend/src/app/beta.component.ts'],
  at: '2026-05-28T08:00:00Z',
};

function diffFor(path: string): string {
  // Echo the requested path into the diff so the rendered .d2h-file-name
  // reflects which file's content is on screen — lets the spec wait for a
  // SPECIFIC file's diff rather than just "any" diff.
  return [
    `diff --git a/${path} b/${path}`,
    `--- a/${path}`,
    `+++ b/${path}`,
    '@@ -1,3 +1,4 @@',
    ' context line',
    '+added line',
    '-removed line',
    '',
  ].join('\n');
}

function makeDetail(): unknown {
  return {
    info: {
      id: JOB_ID, jobKey: `${WATCH_PATH}::${JOB_ID}`, title: 'Perf git view fixture',
      state: '5-human-review', agent: 'claude', cliType: 'claude', model: 'claude-opus-4-7',
      watchPath: WATCH_PATH, projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: null, lastUsage: null, execution: null, order: 1,
      commit: COMMIT, commits: [COMMIT], ownerClientId: 'local-default',
    },
    promptMarkdown: 'Perf git view test prompt.', statusMarkdown: '', log: [],
    promptHistory: [], contextUsage: null, reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

interface Counters { diff: number }

async function installRoutes(page: Page, counters: Counters): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail();

  await page.route('**/api/**', (r) => { r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => { /* ignore late fulfill */ }); });
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, (r) => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ preparation: [], orchestratorPrep: [], needsHumanReview: [], ready: [], progress: [], failedPickup: [], autoReview: [], humanReview: [], completed: [], archive: [] }) }));
  await page.route('**/api/watch-paths**', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]) }));
  await page.route('**/api/workspaces**', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/projects**', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/environment**', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route('**/api/clients', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  // Must be a valid QuotaReport ({ snapshots: [] }); an `{items:[]}` body
  // leaves `snapshots` undefined and HeaderQuotaComponent.cards throws
  // `.find of undefined`, popping a full-screen error-dialog overlay that
  // eats the hover/click pointer events this spec relies on.
  await page.route('**/api/cli/quota**', (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/output(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/runs(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/session-events(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/claude-session(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/hygiene(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projectName: PROJECT, isRepo: true, isDirty: false, hasUpstream: true, ahead: 0, behind: 0, job: { jobId: JOB_ID, state: '5-human-review', jobInfoCommitPresent: true, stampedCommitSha: COMMIT.sha, acceptedTaskUncommitted: false }, error: null }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/status(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isRepo: true, branch: 'main', filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null }) }));

  // Commit-mode diff endpoint — the one the tree-row clicks hit. Count
  // every fetch and inject latency so the round-trip is measurable.
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/(?:commit|commits/[^/]+)/diff\\b`), async (r) => {
    counters.diff += 1;
    const url = new URL(r.request().url());
    const path = url.searchParams.get('path') ?? COMMIT.files[0];
    await new Promise((res) => setTimeout(res, DIFF_LATENCY_MS));
    await r.fulfill({ status: 200, contentType: 'text/plain', body: diffFor(path) });
  });
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/diff\\?.*`), async (r) => {
    counters.diff += 1;
    const url = new URL(r.request().url());
    const path = url.searchParams.get('path') ?? COMMIT.files[0];
    await new Promise((res) => setTimeout(res, DIFF_LATENCY_MS));
    await r.fulfill({ status: 200, contentType: 'text/plain', body: diffFor(path) });
  });
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commits/[^/]+/files`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ sha: COMMIT.sha, files: COMMIT.files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 })) }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commit(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ commit: COMMIT, files: COMMIT.files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 })) }) }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}(\\?|$)`), (r) => r.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
}

/**
 * Click a tree row by its (basename) label and measure, IN the browser via
 * performance.now(), the time from the click to the diff for `expectFile`
 * being painted into the .d2h-file-name header. Returns elapsed ms.
 */
async function clickAndTime(page: Page, rowLabel: string, expectFile: string): Promise<number> {
  return page.evaluate(async ({ rowLabel, expectFile }) => {
    const rows = Array.from(document.querySelectorAll<HTMLElement>('[data-testid="git-tree-file"]'));
    const row = rows.find((el) => el.textContent?.includes(rowLabel));
    if (!row) throw new Error(`row not found: ${rowLabel}`);
    const base = expectFile.split('/').pop()!;
    const start = performance.now();
    row.click();
    await new Promise<void>((resolve) => {
      const check = () => {
        const name = document.querySelector('[data-testid="git-diff"] .d2h-file-name');
        if (name && name.textContent && name.textContent.includes(base)) resolve();
        else requestAnimationFrame(check);
      };
      check();
    });
    return performance.now() - start;
  }, { rowLabel, expectFile });
}

test.describe('GitView performance — drill-in cache + hover cost', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: false, protocol: true, git: true }));
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
        localStorage.removeItem('taskboard.gitPane.commitHeaderCollapsed');
      } catch { /* private mode */ }
    });
  });

  test('repeat file-open hits the LRU cache (no refetch, <100ms) and hover triggers no fetch', async ({ page }, testInfo) => {
    const counters: Counters = { diff: 0 };
    await installRoutes(page, counters);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);

    const [alpha, beta] = COMMIT.files;
    const alphaBase = alpha.split('/').pop()!;
    const betaBase = beta.split('/').pop()!;

    // On load the pane auto-selects files[0] (alpha) and renders its diff,
    // which lazily imports diff2html and warms the cache for alpha.
    await expect(page.getByTestId('git-tree-file').first()).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-testid="git-diff"] .d2h-file-name')).toContainText(alphaBase, { timeout: 10_000 });
    const afterLoadFetches = counters.diff; // alpha fetched once

    // Drill into beta (not yet viewed) → exactly one new fetch, and the
    // measured time includes the 150ms round-trip.
    const betaColdMs = await clickAndTime(page, betaBase, beta);
    const afterBetaFetches = counters.diff;
    expect(afterBetaFetches, 'opening a new file issues exactly one diff fetch').toBe(afterLoadFetches + 1);

    // Drill back to alpha → CACHE HIT. No new fetch; time-to-content well
    // under the 100ms target because it is a synchronous signal→render.
    const alphaWarmMs = await clickAndTime(page, alphaBase, alpha);
    expect(counters.diff, 'reopening a cached file issues NO new diff fetch').toBe(afterBetaFetches);
    expect(alphaWarmMs, `cached re-open ${alphaWarmMs.toFixed(1)}ms must be < 100ms`).toBeLessThan(100);

    // Drill back to beta → CACHE HIT again. Still no new fetch, still fast.
    const betaWarmMs = await clickAndTime(page, betaBase, beta);
    expect(counters.diff, 'second cached re-open issues NO new diff fetch').toBe(afterBetaFetches);
    expect(betaWarmMs, `cached re-open ${betaWarmMs.toFixed(1)}ms must be < 100ms`).toBeLessThan(100);

    // Hover sequence over both rows (10 passes) must not fetch anything —
    // hover highlight is CSS-only, no JS-driven diff load.
    const beforeHover = counters.diff;
    for (let i = 0; i < 10; i++) {
      await page.getByTestId('git-tree-file').nth(0).hover();
      await page.getByTestId('git-tree-file').nth(1).hover();
    }
    expect(counters.diff, 'a 10x hover sequence issues NO diff fetch').toBe(beforeHover);

    // Persist the measured numbers as review evidence.
    const metrics = {
      diffLatencyMs: DIFF_LATENCY_MS,
      alphaColdFetchedOnLoad: afterLoadFetches,
      betaColdOpenMs: Number(betaColdMs.toFixed(1)),
      alphaCachedReopenMs: Number(alphaWarmMs.toFixed(1)),
      betaCachedReopenMs: Number(betaWarmMs.toFixed(1)),
      totalDiffFetches: counters.diff,
      hoverPassesWithoutFetch: 10,
    };
    await testInfo.attach('gitview-perf-metrics.json', {
      body: JSON.stringify(metrics, null, 2),
      contentType: 'application/json',
    });
    console.log('[gitview-perf] metrics:', JSON.stringify(metrics));

    if (process.env.PERF_RESULTS_DIR) {
      const fs = await import('node:fs');
      fs.writeFileSync(`${process.env.PERF_RESULTS_DIR}/gitview-perf-metrics.json`, JSON.stringify(metrics, null, 2));
    }
  });
});
