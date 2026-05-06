import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Repository-hygiene strip - visible review/completed UI states.
 *
 * The strip shows three signals on jobs in `4-auto-review`,
 * `5-human-review`, `6-completed`, and `7-archive`:
 *  - whether the task carries a platform-owned commit stamp,
 *  - whether the working tree is dirty (accepted task work uncommitted),
 *  - whether the stamped commit is still ahead of the upstream
 *    (push pending).
 *
 * The endpoint surface is pure-read so we mock it directly with
 * `page.route` and screenshot each of the load-bearing states. Any
 * regression in the strip rendering will fail the visual assert below
 * (presence of testids) and leave a screenshot under the job's
 * `results/` folder for human review.
 */

interface OutLine { timestamp: string; stream: string; text: string; }

interface HygieneShape {
  projectName: string;
  repoRoot: string;
  isRepo: boolean;
  branch: string;
  upstream: string | null;
  hasUpstream: boolean;
  ahead: number;
  behind: number;
  isDirty: boolean;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  lastCommitSha: string;
  lastCommitShortSha: string;
  lastCommitSubject: string;
  lastCommitAtUtc: string;
  job: {
    jobId: string;
    state: string;
    jobInfoCommitPresent: boolean;
    stampedCommitSha: string | null;
    acceptedTaskUncommitted: boolean;
    commitUnpushed: boolean;
  } | null;
  error: string | null;
}

function makeJobDetail(jobId: string, watchPath: string, state: string, hasCommit: boolean) {
  return {
    info: {
      id: jobId,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Hygiene strip fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/${state}/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      order: 1,
      commit: hasCommit ? {
        sha: 'abcdef1234567890abcdef1234567890abcdef12',
        shortSha: 'abcdef1',
        message: 'feat: deliver fixture',
        filesChanged: 3,
        files: ['a.ts', 'b.ts', 'c.ts'],
        at: new Date().toISOString()
      } : null
    },
    promptMarkdown: 'Pretend prompt.',
    statusMarkdown: '## Done\n\nWork accepted.\n',
    log: [],
    promptHistory: [],
    summaryState: { status: 'finished', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

function makeHygiene(state: string, kind: 'clean-committed' | 'dirty-after-accept' | 'ahead-unpushed'): HygieneShape {
  const base: HygieneShape = {
    projectName: 'fixture',
    repoRoot: 'C:/fixtures/repo',
    isRepo: true,
    branch: 'main',
    upstream: 'origin/main',
    hasUpstream: true,
    ahead: 0,
    behind: 0,
    isDirty: false,
    stagedCount: 0,
    unstagedCount: 0,
    untrackedCount: 0,
    lastCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
    lastCommitShortSha: 'abcdef1',
    lastCommitSubject: 'feat: deliver fixture',
    lastCommitAtUtc: new Date().toISOString(),
    job: null,
    error: null
  };
  if (kind === 'clean-committed') {
    return {
      ...base,
      job: {
        jobId: 'fixture-job', state,
        jobInfoCommitPresent: true,
        stampedCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
        acceptedTaskUncommitted: false,
        commitUnpushed: false
      }
    };
  }
  if (kind === 'dirty-after-accept') {
    return {
      ...base,
      isDirty: true,
      stagedCount: 1,
      unstagedCount: 2,
      untrackedCount: 1,
      job: {
        jobId: 'fixture-job', state,
        jobInfoCommitPresent: false,
        stampedCommitSha: null,
        acceptedTaskUncommitted: true,
        commitUnpushed: false
      }
    };
  }
  return {
    ...base,
    ahead: 2,
    job: {
      jobId: 'fixture-job', state,
      jobInfoCommitPresent: true,
      stampedCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
      acceptedTaskUncommitted: false,
      commitUnpushed: true
    }
  };
}

async function installFixtureRoutes(
  page: Page,
  target: { id: string; watchPath: string },
  state: string,
  hasCommit: boolean,
  hygiene: HygieneShape
) {
  const detail = JSON.stringify(makeJobDetail(target.id, target.watchPath, state, hasCommit));
  const projectHygiene = JSON.stringify({ ...hygiene, job: null });
  const jobHygiene = JSON.stringify(hygiene);

  // Playwright route handlers fire LIFO: the *most recently registered*
  // matching handler wins. Register the catch-all first so the
  // specifically-targeted routes registered below take precedence.
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });

  // Cross-cutting reads that fire on every navigation. Stub minimally so
  // the app boots cleanly even when the backend is offline (the dev
  // backend is offline by default per AGENTS.md "Dev backend lifecycle").
  await page.route('**/api/jobs', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/jobs/grouped**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [], needsHumanReview: [],
        ready: [], progress: [], failedPickup: [],
        autoReview: [], humanReview: [], completed: [], archive: []
      })
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: 'fixture', path: target.watchPath, rootPath: target.watchPath, repositoryPath: target.watchPath }])
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: projectHygiene }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));

  // Targeted job reads - registered last so they win over the catch-all.
  // Playwright's glob mode treats `?` as a single-character wildcard, so
  // we use regex patterns here to match the literal `?` query separator.
  const idEsc = target.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/jobs/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/jobs/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/jobs/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/jobs/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/jobs/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: jobHygiene }));
  await page.route(new RegExp(`/api/jobs/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: detail }));
}

const TARGET = { id: 'fixture-job', watchPath: 'C:/fixtures/repo' };

// JOB_RESULTS_DIR is set by the agent task orchestrator. When present, the
// reporter copies playwright artifacts into <JOB_RESULTS_DIR>/playwright/.
// We additionally drop our hand-curated screenshots straight into
// <JOB_RESULTS_DIR>/ so they live next to the Activity Log evidence the
// reviewer expects.
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissAnyErrorDialog(page: Page) {
  // The app's global error-dialog overlay intercepts pointer events when
  // any backend probe fails. Our route handlers cover every /api/ call we
  // know about, but a transient pre-mock request from the page.goto can
  // still trigger one. Close it before continuing so the pane toggles
  // are clickable.
  const close = page.locator('.error-dialog__close').first();
  for (let i = 0; i < 3; i++) {
    if (!(await close.isVisible().catch(() => false))) return;
    await close.click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

async function ensureProtocolPaneOpen(page: Page) {
  await dismissAnyErrorDialog(page);
  // The pane-toggle-bar persists visibility to localStorage. A prior run
  // that closed the Protocol pane will leave it hidden for us; open it
  // explicitly so the strip's host pane is mounted.
  if (!(await page.getByTestId('pane-protocol').isVisible())) {
    const toggle = page.getByTestId('pane-toggle-protocol');
    if (await toggle.isVisible()) await toggle.click();
  }
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
}

async function captureStrip(page: Page, name: string) {
  const strip = page.getByTestId('hygiene-strip');
  await expect(strip).toBeVisible({ timeout: 5_000 });
  if (RESULTS_DIR) {
    const out = path.join(RESULTS_DIR, `hygiene-strip-${name}.png`);
    await strip.screenshot({ path: out });
  }
}

test.describe('Repository hygiene - review/completed strip', () => {
  test.beforeEach(async ({ page }) => {
    // Ensure the Protocol pane is enabled so the hygiene strip's host
    // pane mounts, regardless of localStorage state from prior runs.
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
      } catch { /* private mode */ }
    });
  });

  test('clean committed task: ✓ task committed, ✓ tree clean, ✓ in sync', async ({ page }) => {
    const target = TARGET;
    await installFixtureRoutes(page, target, '6-completed', true, makeHygiene('6-completed', 'clean-committed'));

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await ensureProtocolPaneOpen(page);
    await expect(page.getByTestId('hygiene-strip')).toBeVisible({ timeout: 10_000 });
    // Icon-only strip: state lives on the title attribute (hover tooltip)
    // since the visible glyph is a single character. No information is
    // lost — the tooltip carries the same wording as the old verbose row.
    await expect(page.getByTestId('hygiene-commit')).toHaveAttribute('title', /Task committed/i);
    await expect(page.getByTestId('hygiene-tree')).toHaveAttribute('title', /Working tree clean/i);
    await expect(page.getByTestId('hygiene-push')).toHaveAttribute('title', /In sync/i);
    await expect(page.getByTestId('hygiene-warning-dirty-after-accept')).toHaveCount(0);
    await expect(page.getByTestId('hygiene-warning-unpushed')).toHaveCount(0);
    await captureStrip(page, 'clean-committed');
  });

  test('dirty accepted task: ⚠ no task commit, ⚠ working tree dirty, manual-commit action visible', async ({ page }) => {
    const target = TARGET;
    await installFixtureRoutes(page, target, '5-human-review', false, makeHygiene('5-human-review', 'dirty-after-accept'));

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await ensureProtocolPaneOpen(page);
    await expect(page.getByTestId('hygiene-strip')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('hygiene-commit')).toHaveAttribute('title', /No task commit/i);
    await expect(page.getByTestId('hygiene-tree')).toHaveAttribute('title', /Working tree dirty/i);
    await expect(page.getByTestId('hygiene-warning-dirty-after-accept')).toBeVisible();
    await expect(page.getByTestId('hygiene-commit-accepted')).toBeVisible();
    await captureStrip(page, 'dirty-after-accept');
  });

  test('ahead/unpushed state: ✓ committed but ↑ N ahead, push-pending warning visible', async ({ page }) => {
    const target = TARGET;
    await installFixtureRoutes(page, target, '6-completed', true, makeHygiene('6-completed', 'ahead-unpushed'));

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await ensureProtocolPaneOpen(page);
    await expect(page.getByTestId('hygiene-strip')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('hygiene-commit')).toHaveAttribute('title', /Task committed/i);
    await expect(page.getByTestId('hygiene-push')).toHaveAttribute('title', /2 commits ahead/i);
    await expect(page.getByTestId('hygiene-warning-unpushed')).toBeVisible();
    await expect(page.getByTestId('project-hygiene-badge')).toContainText(/unpushed/i);
    await captureStrip(page, 'ahead-unpushed');
  });

  test('hygiene strip is not rendered for in-progress jobs', async ({ page }) => {
    const target = TARGET;
    await installFixtureRoutes(page, target, '3-progress', false, makeHygiene('3-progress', 'dirty-after-accept'));

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);
    await ensureProtocolPaneOpen(page);
    await expect(page.getByTestId('hygiene-strip')).toHaveCount(0);
  });
});
