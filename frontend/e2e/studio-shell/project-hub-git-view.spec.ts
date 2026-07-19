import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Project Hub Git View (AGT-1807) — mocked full-stack drive.
 *
 * The Project Hub exposes a dedicated, read-only Git View page: a grouped
 * branch / worktree / recent-history tree on the left, and a detail + diff
 * pane on the right that reuses the shared diff renderer. This spec mounts the
 * Project Hub straight onto the `git` rail (persisted studio-tab state), with
 * every backend route mocked, and asserts each acceptance bullet:
 *   - the Git View rail is present in the Project Hub navigation and active;
 *   - the tree distinguishes main / develop / feature / task branches and lists
 *     the on-disk worktree/checkout folders;
 *   - selecting a recent commit loads its files and renders the diff through the
 *     shared <app-diff-content> renderer;
 *   - selecting a branch shows its detail card.
 *
 * All API traffic is stubbed, so no live backend is required; the screenshot
 * saved to results/ is therefore labelled `--mocked`.
 */

const PROJECT = 'demo-project';
const REPO_PATH = 'C:/repo/demo-project';
const HUB_TAB_KEY = `hub:${PROJECT}`;
const BOOT_TIMEOUT = 60_000;

const COMMIT_SHA = 'c'.repeat(40);

const INVENTORY = {
  projectName: PROJECT,
  repositoryPath: REPO_PATH,
  isRepo: true,
  currentBranch: 'main',
  worktrees: [
    { path: REPO_PATH, branch: 'main', headSha: 'a'.repeat(40), headShortSha: 'aaaaaaa', isPrimary: true, isDetached: false, isBare: false },
    { path: 'C:/repo/demo-project-task-1', branch: 'task/1', headSha: 'b'.repeat(40), headShortSha: 'bbbbbbb', isPrimary: false, isDetached: false, isBare: false },
  ],
  branches: [
    { name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: true, upstream: 'origin/main', ahead: 0, behind: 0, lastCommitSubject: 'seed', lastCommitAtUtc: '2026-07-01T00:00:00Z', worktreePath: REPO_PATH },
    { name: 'develop', category: 'develop', tipSha: 'd'.repeat(40), tipShortSha: 'ddddddd', isCurrent: false, upstream: 'origin/develop', ahead: 1, behind: 0, lastCommitSubject: 'dev work', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: null },
    { name: 'feature/login', category: 'feature', tipSha: 'e'.repeat(40), tipShortSha: 'eeeeeee', isCurrent: false, upstream: null, ahead: 0, behind: 0, lastCommitSubject: 'feat: login form', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: null },
    { name: 'task/1', category: 'task', tipSha: 'b'.repeat(40), tipShortSha: 'bbbbbbb', isCurrent: false, upstream: null, ahead: 2, behind: 0, lastCommitSubject: 'task work', lastCommitAtUtc: '2026-07-03T00:00:00Z', worktreePath: 'C:/repo/demo-project-task-1' },
  ],
  recentCommits: [
    { sha: COMMIT_SHA, shortSha: 'ccccccc', authorDateUtc: '2026-07-03T10:00:00Z', author: 'dev', subject: 'feat: add thing', filesChanged: 1, added: 3, removed: 1 },
    { sha: 'f'.repeat(40), shortSha: 'fffffff', authorDateUtc: '2026-07-02T09:00:00Z', author: 'dev', subject: 'chore: cleanup', filesChanged: 2, added: 5, removed: 2 },
  ],
  error: null,
};

function diffBody(filePath: string): string {
  return [
    `diff --git a/${filePath} b/${filePath}`,
    `--- a/${filePath}`,
    `+++ b/${filePath}`,
    '@@ -1,2 +1,3 @@',
    ' const a = 1;',
    '+const b = 2;',
    '-const c = 3;',
    '',
  ].join('\n');
}

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  // Catch-all first (returns []); specific routes registered afterwards win.
  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', r => r.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));

  // The Git View endpoints under test.
  await page.route('**/api/git/inventory**', r => r.fulfill(json(INVENTORY)));
  await page.route('**/api/git/project-commit/files**', r => {
    const sha = new URL(r.request().url()).searchParams.get('sha') ?? COMMIT_SHA;
    return r.fulfill(json({ sha, files: [{ status: 'M', path: 'src/thing.ts', added: 3, removed: 1 }] }));
  });
  await page.route('**/api/git/project-commit/diff**', r => {
    const p = new URL(r.request().url()).searchParams.get('path') ?? 'src/thing.ts';
    return r.fulfill(json({ diff: diffBody(p), hasDiff: true, emptyReason: null }));
  });
}

/** Seed a persisted Project Hub tab pinned to the git rail, then reload. */
async function openHubOnGit(page: Page): Promise<void> {
  await page.evaluate(({ tabKey, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__' },
        { kind: 'hub', projectName: project, section: 'git' },
      ],
      activeKey: tabKey,
    }));
    history.replaceState(null, '', '/');
  }, { tabKey: HUB_TAB_KEY, project: PROJECT });
  await page.reload();
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

function resultsDir(): string {
  const fromEnv = process.env.GIT_VIEW_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-hub-git-view');
}

test.describe('Project Hub · Git View (mocked)', () => {
  test.setTimeout(180_000);

  test('opens from the Project Hub rail, browses the tree, and renders a commit diff', async ({ page }, testInfo) => {
    await installRoutes(page);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });

    await openHubOnGit(page);

    // The Project Hub is open on the Git View rail.
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('project-shell-rail-git')).toBeVisible();

    const panel = page.getByTestId('project-git-panel');
    await expect(panel).toBeVisible({ timeout: 15_000 });

    // Repository path + branch/worktree/history groups are shown.
    await expect(page.getByTestId('git-repo-path')).toContainText(REPO_PATH);
    await expect(page.getByTestId('git-tree-group-worktrees')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-integration')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-feature')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-task')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-history')).toBeVisible();

    // Branch rows carry their category badge; main is the current branch.
    await expect(page.locator('[data-testid="git-branch-row"][data-branch="main"]')).toContainText('main');
    await expect(page.locator('[data-testid="git-branch-row"][data-branch="task/1"]')).toContainText('task');

    // Select a recent commit → files load and the shared diff renderer shows it.
    await page.locator(`[data-testid="git-commit-row"][data-sha="${COMMIT_SHA}"]`).click();
    await expect(page.getByTestId('git-detail-card')).toContainText('feat: add thing');
    await expect(page.getByTestId('git-file-row').first()).toContainText('src/thing.ts');
    await expect(page.getByTestId('git-diff')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="git-diff"] .d2h-file-name')).toContainText('thing.ts', { timeout: 15_000 });

    // Evidence screenshot (mocked API).
    fs.mkdirSync(resultsDir(), { recursive: true });
    const shotPath = path.join(resultsDir(), 'project-hub-git-view--mocked.png');
    await page.screenshot({ path: shotPath, fullPage: true });
    await testInfo.attach('project-hub-git-view--mocked.png', { path: shotPath, contentType: 'image/png' });

    // Selecting a branch swaps the detail card to that branch.
    await page.locator('[data-testid="git-branch-row"][data-branch="task/1"]').click();
    await expect(page.getByTestId('git-detail-card')).toContainText('task/1');
  });

  // AGT-2011: the Git View is a full-bleed tool surface — it must fill the whole
  // Project Hub panel (no shared max-width cap, no outer panel padding) so the
  // tree + diff use the entire viewport. Regression guard for the project-shell
  // `data-rail-key='git'` opt-in; without it the panel reverts to the padded,
  // 1280px-capped prose column that left the lower half of the view empty.
  test('git panel fills the whole hub panel (no max-width cap, no padding gap)', async ({ page }) => {
    // Wide viewport so the panel itself is comfortably wider than the shared
    // 1280px content cap — that is the only regime where the cap would bite.
    await page.setViewportSize({ width: 2000, height: 1000 });
    await installRoutes(page);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
    await openHubOnGit(page);

    const panel = page.getByTestId('project-shell-panel-git');
    const gitPanel = page.getByTestId('project-git-panel');
    await expect(gitPanel).toBeVisible({ timeout: 15_000 });

    const panelBox = await panel.boundingBox();
    const gitBox = await gitPanel.boundingBox();
    expect(panelBox).not.toBeNull();
    expect(gitBox).not.toBeNull();

    // Sanity: the panel is wide enough that a 1280px cap would leave a visible gap.
    expect(panelBox!.width).toBeGreaterThan(1300);
    // Fills the full width: the panel drops its inline padding and the shared
    // 1280px content cap for this rail, so the git surface spans the panel edge
    // to edge (allow a few px for sub-pixel rounding).
    expect(gitBox!.width).toBeGreaterThan(panelBox!.width - 4);
    // Proof the cap is gone (a capped surface would pin at ≈1280).
    expect(gitBox!.width).toBeGreaterThan(1300);
    // Fills the full height: the panes reach the bottom instead of leaving the
    // lower half of the viewport empty.
    expect(gitBox!.height).toBeGreaterThan(panelBox!.height - 4);
  });
});
