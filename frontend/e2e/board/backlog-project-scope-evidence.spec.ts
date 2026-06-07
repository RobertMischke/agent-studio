import { test, expect, type Page } from '@playwright/test';

/**
 * Visual-evidence spec for the bug "Backlog/Page not scoped to the active
 * project - shows foreign elements (count 193 symptom)".
 *
 * Fully mocked via route interception so it runs against any served frontend
 * (dev build, no real backend required). The grouped payload deliberately
 * carries TWO projects:
 *   - "Agent Task Processor" (ATP): 3 real backlog tasks (mirrors ASS-717/718/719)
 *   - "Lotta Dashboard": 2 foreign backlog tasks + 2 foreign human-review tasks
 *     (the "156 human-review" cross-project bleed symptom, scaled down)
 *
 * The active studio tab is a backlog tab scoped to ATP, so the triage screen
 * must show EXACTLY the 3 ATP backlog rows: no Lotta backlog, no Lotta
 * human-review, and the header count must read "3 tasks · Agent Task Processor".
 * The screenshot is written to the job's results/ folder as the visual proof.
 */

const ATP = 'Agent Task Processor';
const LOTTA = 'Lotta Dashboard';
const ATP_PATH = 'C:/fixtures/atp-repo';
const LOTTA_PATH = 'C:/fixtures/lotta-repo';

function makeTask(id: string, projectName: string, watchPath: string, state: string, title: string) {
  return {
    id,
    taskKey: `${watchPath}::${id}`,
    title,
    state,
    order: 0,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-01T09:00:00Z',
    watchPath,
    projectName,
    folderPath: `${watchPath}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: null,
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    taskType: 'chore',
  };
}

const ATP_BACKLOG = [
  makeTask('ASS-717', ATP, ATP_PATH, '0-backlog', 'ATP backlog item seven-one-seven'),
  makeTask('ASS-718', ATP, ATP_PATH, '0-backlog', 'ATP backlog item seven-one-eight'),
  makeTask('ASS-719', ATP, ATP_PATH, '0-backlog', 'ATP backlog item seven-one-nine'),
];
const LOTTA_BACKLOG = [
  makeTask('LOT-1', LOTTA, LOTTA_PATH, '0-backlog', 'Lotta foreign backlog one'),
  makeTask('LOT-2', LOTTA, LOTTA_PATH, '0-backlog', 'Lotta foreign backlog two'),
];
const LOTTA_HUMAN_REVIEW = [
  makeTask('LOT-H1', LOTTA, LOTTA_PATH, '5-human-review', 'Lotta foreign human review one'),
  makeTask('LOT-H2', LOTTA, LOTTA_PATH, '5-human-review', 'Lotta foreign human review two'),
];

const GROUPED_PAYLOAD = {
  backlog: [...ATP_BACKLOG, ...LOTTA_BACKLOG],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: LOTTA_HUMAN_REVIEW,
  completed: [],
  archive: [],
};

async function installRoutes(page: Page) {
  // Catch-all FIRST: Playwright matches the most-recently-registered route
  // first, so every specific route below overrides this default. Anything
  // unmocked returns an empty array so the boot sequence never hangs.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: ATP, path: ATP_PATH, rootPath: ATP_PATH, repositoryPath: ATP_PATH },
        { name: LOTTA, path: LOTTA_PATH, rootPath: LOTTA_PATH, repositoryPath: LOTTA_PATH },
      ]),
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-01T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-01T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [ATP]: { projectName: ATP, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
          [LOTTA]: { projectName: LOTTA, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function seedBacklogTab(page: Page) {
  await page.addInitScript((project) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'backlog', projectName: project }],
      activeKey: `backlog:${project}`,
    }));
  }, ATP);
}

test.describe('Backlog triage is strictly scoped to the active project', () => {
  test('shows only the scoped project and screenshots the evidence', async ({ page }, testInfo) => {
    await seedBacklogTab(page);
    await installRoutes(page);
    await page.goto('/?includeFixtures=true');
    await page.waitForLoadState('domcontentloaded');

    const screen = page.locator('[data-testid="backlog-triage-screen"]');
    await expect(screen).toBeVisible({ timeout: 15_000 });

    // Exactly the 3 ATP backlog rows render - no foreign Lotta tasks.
    const rows = page.locator('[data-testid="backlog-triage-row"]');
    await expect(rows).toHaveCount(3);
    const keys = await rows.evaluateAll((els) =>
      els.map((e) => (e as HTMLElement).getAttribute('data-task-id')).sort());
    expect(keys).toEqual(['ASS-717', 'ASS-718', 'ASS-719']);

    // Every visible project chip names the scoped project only.
    const projectChips = page.locator('[data-testid="backlog-triage-row-project"]');
    const chipNames = await projectChips.allTextContents();
    for (const name of chipNames) {
      expect(name).toContain(ATP);
      expect(name).not.toContain(LOTTA);
    }

    // Header count == 3 tasks, scoped to ATP (the "193" symptom is gone).
    const subtitle = page.locator('[data-testid="backlog-triage-count"]');
    await expect(subtitle).toContainText('3 tasks');
    await expect(subtitle).toContainText(ATP);

    // No foreign Lotta title (backlog or human-review) leaked into the DOM.
    await expect(page.getByText('Lotta foreign', { exact: false })).toHaveCount(0);

    // Strip any Vite hot-reload error overlay before the frame.
    await page.evaluate(() => {
      document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
    });
    await page.setViewportSize({ width: 1500, height: 1000 });

    const buf = await page.screenshot({ fullPage: false });
    await testInfo.attach('backlog-scoped-to-active-project.png', { body: buf, contentType: 'image/png' });
    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await page.screenshot({ path: `${resultsDir}/backlog-scoped-to-active-project.png`, fullPage: false });
    }
    await page.screenshot({ path: 'test-results/backlog-scoped-to-active-project.png', fullPage: false });
  });
});
