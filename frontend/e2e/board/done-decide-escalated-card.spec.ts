import { test, expect, type Page } from '@playwright/test';

/**
 * "Failed-Cards sehen aus wie Done" — Option C.
 *
 * The "Done & Decide" super-column stacks the human-review lane next to
 * Completed/Archive. An escalated card (orchestratorVerdict=escalate,
 * statusMarkdown "Result: Failed") parked in 5-human-review used to render
 * identically to a finished card, hiding that a human still has to act.
 *
 * This spec locks the fix:
 *  - an escalated human-review card carries the `task-card--attention`
 *    treatment (red left ribbon + tint) and an explicit "Escalated" pill,
 *  - its surface + left-border are visually distinct from a Completed card
 *    sitting in the same super-column,
 *  - an accepted card gets the calm "Ready to sign off" pill but NOT the
 *    attention treatment,
 *  - the differentiation survives the light theme and a narrow (mobile)
 *    viewport (acceptance criterion 3).
 *
 * Routes are fully mocked so the test is deterministic and does not depend
 * on the shared dev board's mutable contents.
 */

const PROJECT = 'fixture-done-decide';
const WATCH_PATH = 'C:/fixtures/done-decide';

interface JobOverride {
  id?: string;
  title?: string;
  state?: string;
  order?: number;
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
}

function makeJob(overrides: JobOverride = {}) {
  const id = overrides.id ?? 'dd-card';
  const state = overrides.state ?? '5-human-review';
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: overrides.title ?? 'Done & Decide fixture',
    state,
    order: overrides.order ?? 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-05-30T07:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-30T07:30:00Z',
    sessionName: null,
    model: 'gpt-5-codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    orchestratorVerdict: overrides.orchestratorVerdict ?? null,
  };
}

const ESCALATED = makeJob({ id: 'dd-escalate', title: 'Escalated fixture card', orchestratorVerdict: 'escalate', order: 1 });
const ACCEPTED = makeJob({ id: 'dd-accept', title: 'Accept fixture card', orchestratorVerdict: 'accept', order: 2 });
const PLAIN_REVIEW = makeJob({ id: 'dd-plain', title: 'Plain review fixture card', orchestratorVerdict: null, order: 3 });
const COMPLETED = makeJob({ id: 'dd-done', title: 'Completed fixture card', state: '6-completed', order: 1 });

const HUMAN_REVIEW = [ESCALATED, ACCEPTED, PLAIN_REVIEW];
const ALL_JOBS = [...HUMAN_REVIEW, COMPLETED];

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  needsHumanReview: [],
  ready: [],
  progress: [],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: HUMAN_REVIEW,
  completed: [COMPLETED],
  archive: [],
};

async function installRoutes(page: Page) {
  // Specific routes are registered AFTER the catch-all so they take
  // precedence (Playwright matches most-recently-added first).
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  await page.route('**/api/tasks/grouped**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));
  await page.route(/\/api\/tasks(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(ALL_JOBS) }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
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
  await page.route(/\/api\/runner\/status(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function dismissDevOverlays(page: Page): Promise<void> {
  // The shared dev working tree may carry unrelated in-progress edits that
  // trip a dev-only compile/runtime error overlay. Strip that environmental
  // noise so the evidence screenshots show the board, not someone else's
  // half-finished work. No-op in a clean tree.
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach((el) => el.remove());
  });
  await page.keyboard.press('Escape').catch(() => undefined);
  await page.keyboard.press('Escape').catch(() => undefined);
}

async function bootBoard(page: Page) {
  await seedBoardTab(page);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
  await dismissDevOverlays(page);
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title }).first();
}

function resultsScreenshot(page: Page, name: string) {
  if (process.env.DONE_DECIDE_RESULTS_DIR) {
    return page.screenshot({ path: `${process.env.DONE_DECIDE_RESULTS_DIR}/${name}`, fullPage: false });
  }
  return Promise.resolve(Buffer.from(''));
}

test.describe('Done & Decide — escalated cards do not look like Done', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`escalated human-review card is visually separated from a Completed card (${theme})`, async ({ page }, testInfo) => {
      await bootBoard(page);
      await setTheme(page, theme);
      await page.waitForTimeout(300);

      const escalated = cardByTitle(page, 'Escalated fixture card');
      const completed = cardByTitle(page, 'Completed fixture card');
      const accepted = cardByTitle(page, 'Accept fixture card');
      await expect(escalated).toBeVisible({ timeout: 5_000 });
      await expect(completed).toBeVisible();

      // 1. The escalated card wears the attention treatment + explicit pill.
      await expect(escalated).toHaveClass(/task-card--attention/);
      const pill = escalated.locator('[data-testid="task-card-human-review"]');
      await expect(pill).toBeVisible();
      await expect(pill).toHaveText(/Escalated/);
      await expect(pill).toHaveClass(/task-card__human-review-pill--attention/);

      // 2. Surface + left border are distinct from the Completed card. The
      //    Completed card must NOT carry the attention class, and the
      //    escalated card's left ribbon resolves to a different colour than
      //    the completed card's (red failure accent vs. green).
      await expect(completed).not.toHaveClass(/task-card--attention/);
      const [escBg, doneBg] = await Promise.all([
        escalated.evaluate((el) => getComputedStyle(el).backgroundColor),
        completed.evaluate((el) => getComputedStyle(el).backgroundColor),
      ]);
      expect(escBg, `[${theme}] escalated vs completed surface`).not.toBe(doneBg);
      const [escBorder, doneBorder] = await Promise.all([
        escalated.evaluate((el) => getComputedStyle(el).borderLeftColor),
        completed.evaluate((el) => getComputedStyle(el).borderLeftColor),
      ]);
      expect(escBorder, `[${theme}] escalated vs completed left ribbon`).not.toBe(doneBorder);

      // 3. The accepted card is calm: sign-off pill, no attention treatment.
      await expect(accepted).not.toHaveClass(/task-card--attention/);
      const acceptPill = accepted.locator('[data-testid="task-card-human-review"]');
      await expect(acceptPill).toHaveText(/Ready to sign off/);
      await expect(acceptPill).toHaveClass(/task-card__human-review-pill--accept/);

      await testInfo.attach(`done-decide-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await resultsScreenshot(page, `done-decide-${theme}.png`);
    });
  }

  test('attention treatment survives a narrow (mobile) viewport', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await bootBoard(page);
    await setTheme(page, 'dark');

    // Free the board width for the evidence shot. The shell's body grid
    // reserves a fixed sidebar track (minmax(0, sidebarWidth)px) whenever the
    // explorer rail is open, squeezing the board to a sliver at 390px — and
    // panel visibility is not persisted, so it can't be seeded collapsed.
    // Clicking the active panel toggle hides the rail; Angular drops the
    // <aside> via @if and the grid reflows the board track to 1fr. This is a
    // screenshot-framing concern only and does not touch the card under test.
    await page.locator('[data-testid="studio-activity-bar"] .studio-ab__btn--active[data-panel]')
      .click({ timeout: 2_000 })
      .catch(() => undefined);
    await page.waitForTimeout(300);

    const escalated = cardByTitle(page, 'Escalated fixture card');
    await escalated.scrollIntoViewIfNeeded();
    await expect(escalated).toBeVisible({ timeout: 5_000 });
    await expect(escalated).toHaveClass(/task-card--attention/);
    await expect(escalated.locator('[data-testid="task-card-human-review"]')).toHaveText(/Escalated/);

    // Element-level capture as the primary evidence: it proves the red ribbon
    // + Escalated pill still render at mobile width regardless of shell layout.
    // The full-page shot below (with chrome hidden) provides board context.
    const cardShot = await escalated.screenshot();
    await testInfo.attach('done-decide-mobile-card.png', { body: cardShot, contentType: 'image/png' });
    if (process.env.DONE_DECIDE_RESULTS_DIR) {
      await escalated.screenshot({ path: `${process.env.DONE_DECIDE_RESULTS_DIR}/done-decide-mobile-card.png` });
    }
    await testInfo.attach('done-decide-mobile.png', {
      body: await page.screenshot({ fullPage: false }),
      contentType: 'image/png',
    });
    await resultsScreenshot(page, 'done-decide-mobile.png');
  });
});
