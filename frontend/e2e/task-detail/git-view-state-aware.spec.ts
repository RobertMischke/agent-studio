import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * F55: State-aware Git View display modes.
 *
 * The run-git-viewer component renders an inline panel below the run
 * timeline that switches between three modes based on the job's state:
 *
 * 1. **Committed** (4-auto-review, 5-human-review, 6-completed): compact
 *    hash cards with commit message, author time, and on-demand diff.
 * 2. **Worktree** (3-progress): live working-tree status with auto-poll.
 * 3. **Idle** (0-backlog, 1-*, 2-ready): empty state placeholder.
 *
 * Each scenario is tested with fully-mocked API routes so there is no
 * dependency on a running backend or real git repository.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/state-aware-git';

const COMMITS = [
  {
    sha: 'aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111',
    shortSha: 'aaaa111',
    message: 'feat(settings): F47 cleanup',
    filesChanged: 3,
    files: ['src/settings.ts', 'src/settings.spec.ts', 'docs/settings.md'],
    at: '2026-05-24T08:00:00Z',
  },
  {
    sha: 'bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222',
    shortSha: 'bbbb222',
    message: 'feat(board): theme-aware job-card borders',
    filesChanged: 2,
    files: ['src/card.ts', 'src/card.scss'],
    at: '2026-05-24T11:00:00Z',
  },
  {
    sha: 'cccc3333cccc3333cccc3333cccc3333cccc3333',
    shortSha: 'cccc333',
    message: 'fix: WCAG contrast on diff lines in light theme',
    filesChanged: 1,
    files: ['src/diff.scss'],
    at: '2026-05-24T14:30:00Z',
  },
];

function makeDetail(state: string, includeCommits: boolean) {
  const newest = includeCommits ? COMMITS[COMMITS.length - 1] : null;
  return {
    info: {
      id: 'git-view-state-test',
      jobKey: `${WATCH_PATH}::git-view-state-test`,
      title: 'State-aware git view fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/git-view-state-test`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: newest,
      commits: includeCommits ? COMMITS : [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

const JOB_ID = 'git-view-state-test';

async function installRoutes(
  page: Page,
  state: string,
  includeCommits: boolean,
  gitStatus?: object,
) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state, includeCommits);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/jobs', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/jobs/grouped**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [],
        orchestratorPrep: [],
        needsHumanReview: [],
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
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
    }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
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

  await page.route(new RegExp(`/api/jobs/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/jobs/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ runs: [] }),
    }),
  );
  await page.route(new RegExp(`/api/jobs/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/jobs/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  );
  await page.route(new RegExp(`/api/jobs/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: PROJECT,
        isRepo: true,
        isDirty: false,
        hasUpstream: true,
        ahead: 0,
        behind: 0,
        job: {
          jobId: JOB_ID,
          state,
          jobInfoCommitPresent: includeCommits,
          stampedCommitSha: includeCommits ? COMMITS[2].sha : null,
          acceptedTaskUncommitted: false,
        },
        error: null,
      }),
    }),
  );
  await page.route(new RegExp(`/api/jobs/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(
        gitStatus ?? {
          isRepo: true,
          branch: 'main',
          filesChanged: 0,
          totalAdded: 0,
          totalRemoved: 0,
          files: [],
          error: null,
        },
      ),
    }),
  );
  await page.route(new RegExp(`/api/jobs/${idEsc}/commit(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        commit: includeCommits ? COMMITS[2] : null,
        files: includeCommits
          ? COMMITS[2].files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 }))
          : [],
      }),
    }),
  );
  for (const c of COMMITS) {
    await page.route(
      new RegExp(`/api/jobs/${idEsc}/commits/${c.sha}/diff(\\?|$)`),
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            diff: `diff --git a/${c.files[0]} b/${c.files[0]}\n--- a/${c.files[0]}\n+++ b/${c.files[0]}\n@@ -1,3 +1,5 @@\n context line\n+added by ${c.shortSha}\n-removed line`,
          }),
        }),
    );
  }
  await page.route(new RegExp(`/api/jobs/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(detail),
    }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

test.describe('F55: Git View state-aware display', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: false, protocol: true, git: false }),
        );
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
      } catch {
        /* private mode */
      }
    });
  });

  test('committed mode (single commit) shows one hash card', async ({ page }) => {
    const singleCommit = [COMMITS[2]];
    const detail = makeDetail('4-auto-review', true);
    detail.info.commits = singleCommit;
    detail.info.commit = singleCommit[0];

    await installRoutes(page, '4-auto-review', true);
    // Override the detail route with single-commit variant
    const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    await page.route(new RegExp(`/api/jobs/${idEsc}(\\?|$)`), (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(detail),
      }),
    );

    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    const inline = page.getByTestId('rgv-inline-committed');
    await expect(inline).toBeVisible({ timeout: 10_000 });

    const cards = page.getByTestId('rgv-commit-card');
    await expect(cards).toHaveCount(1);
    await expect(cards.first()).toContainText(COMMITS[2].shortSha.slice(0, 7));

    if (RESULTS_DIR) {
      await dismissErrorDialog(page);
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'f55-git-view-committed-single-light.png'),
      });
    }
  });

  test('committed mode (multi commit) shows all cards, newest first', async ({ page }) => {
    await installRoutes(page, '6-completed', true);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    const inline = page.getByTestId('rgv-inline-committed');
    await expect(inline).toBeVisible({ timeout: 10_000 });
    await expect(inline).toContainText('Committed');
    await expect(inline).toContainText('3 commits');

    const cards = page.getByTestId('rgv-commit-card');
    await expect(cards).toHaveCount(3);

    // Newest first (reversed from the API order)
    await expect(cards.nth(0)).toHaveAttribute('data-sha', COMMITS[2].sha);
    await expect(cards.nth(1)).toHaveAttribute('data-sha', COMMITS[1].sha);
    await expect(cards.nth(2)).toHaveAttribute('data-sha', COMMITS[0].sha);

    await expect(cards.nth(0)).toContainText('WCAG contrast');
    await expect(cards.nth(2)).toContainText('F47 cleanup');

    if (RESULTS_DIR) {
      await dismissErrorDialog(page);
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'f55-git-view-committed-multi-light.png'),
      });
    }
  });

  test('committed mode: clicking card toggles inline diff', async ({ page }) => {
    await installRoutes(page, '5-human-review', true);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    const inline = page.getByTestId('rgv-inline-committed');
    await expect(inline).toBeVisible({ timeout: 10_000 });

    // Click via evaluate to bypass any error-dialog overlay from
    // HeaderQuotaComponent that fires on mocked quota endpoints.
    await page.evaluate((sha) => {
      const cards = Array.from(
        document.querySelectorAll<HTMLElement>('[data-testid="rgv-commit-card"]'),
      );
      const target = cards.find((c) => c.getAttribute('data-sha') === sha);
      target?.querySelector('button')?.click();
    }, COMMITS[2].sha);

    const diffArea = page.getByTestId('rgv-commit-diff-area');
    await expect(diffArea).toBeVisible({ timeout: 5_000 });

    const diffBody = page.getByTestId('rgv-commit-diff-body');
    await expect(diffBody).toBeVisible();
    await expect(diffBody).toContainText(COMMITS[2].shortSha);

    // Click again to collapse
    await page.evaluate((sha) => {
      const cards = Array.from(
        document.querySelectorAll<HTMLElement>('[data-testid="rgv-commit-card"]'),
      );
      const target = cards.find((c) => c.getAttribute('data-sha') === sha);
      target?.querySelector('button')?.click();
    }, COMMITS[2].sha);

    await expect(diffArea).not.toBeVisible();
  });

  test('worktree mode shows live status with file list', async ({ page }) => {
    const gitStatus = {
      isRepo: true,
      branch: 'main',
      filesChanged: 3,
      totalAdded: 12,
      totalRemoved: 4,
      files: [
        { status: 'M', path: 'src/component.ts', added: 8, removed: 3 },
        { status: 'A', path: 'src/component.spec.ts', added: 4, removed: 0 },
        { status: 'D', path: 'src/old-helper.ts', added: 0, removed: 1 },
      ],
      error: null,
    };

    await installRoutes(page, '3-progress', false, gitStatus);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    const inline = page.getByTestId('rgv-inline-worktree');
    await expect(inline).toBeVisible({ timeout: 10_000 });
    await expect(inline).toContainText('Live worktree');
    await expect(inline).toContainText('main');
    await expect(inline).toContainText('3 files');
    await expect(inline).toContainText('+12');
    await expect(inline).toContainText('-4');
    await expect(inline).toContainText('src/component.ts');
    await expect(inline).toContainText('src/component.spec.ts');
    await expect(inline).toContainText('src/old-helper.ts');

    if (RESULTS_DIR) {
      await dismissErrorDialog(page);
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'f55-git-view-worktree-mode-light.png'),
      });
    }
  });

  test('idle mode shows empty state for backlog task', async ({ page }) => {
    await installRoutes(page, '2-ready', false);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    const inline = page.getByTestId('rgv-inline-idle');
    await expect(inline).toBeVisible({ timeout: 10_000 });
    await expect(inline).toContainText("No commits yet");

    if (RESULTS_DIR) {
      await dismissErrorDialog(page);
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'f55-git-view-idle-light.png'),
      });
    }
  });

  test('worktree mode shows clean state when no changes', async ({ page }) => {
    await installRoutes(page, '3-progress', false);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    const inline = page.getByTestId('rgv-inline-worktree');
    await expect(inline).toBeVisible({ timeout: 10_000 });
    await expect(inline).toContainText('Working tree clean');
  });
});
