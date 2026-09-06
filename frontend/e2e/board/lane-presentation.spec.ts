import { expect, Page, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const WATCH_PATH = 'C:/fixtures/lane-presentation';
const PROJECT = 'lane-presentation';
const JOB_ID = 'human-review-presentation';
const LANE_STATE = '5-human-review';
const SHOTS_DIR = process.env['JOB_RESULTS_DIR']
  ?? resolve(__dirname, '../../test-results/lane-presentation');

function info() {
  return {
    id: JOB_ID,
    key: 'AGT-LANE',
    displayKey: 'AGT-LANE',
    taskKey: `${WATCH_PATH}::${JOB_ID}`,
    jobKey: `${WATCH_PATH}::${JOB_ID}`,
    title: 'One lane identity fixture',
    state: LANE_STATE,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${LANE_STATE}/${JOB_ID}`,
    createdAt: '2026-09-06T08:00:00Z',
    lastActivity: '2026-09-06T08:10:00Z',
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    orchestratorVerdict: null,
    tags: [],
    taskType: 'feature',
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function detail() {
  return {
    info: info(),
    promptMarkdown: 'Verify lane presentation.',
    statusMarkdown: '# Status\n\n- Result: Success\n\n## What Was Done\n\n- Unified the lane presentation.',
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: { status: 'ready', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => route.abort('failed'));
  await page.route('**/api/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
  }));
  await page.route('**/api/watch-paths**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]),
  }));
  await page.route('**/api/tasks/grouped**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
      failedPickup: [], codeNotComplete: [], autoReview: [], review: [], escalated: [],
      humanReview: [info()], completed: [], archive: [],
    }),
  }));
  await page.route('**/api/runner/status**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ projects: {} }),
  }));
  await page.route('**/api/cli/quota**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ ttlSeconds: 600, snapshots: [] }),
  }));
  await page.route('**/api/tasks/archive**', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ items: [], total: 0 }),
  }));
  await page.route(new RegExp(`/api/tasks/${JOB_ID}(?:\\?|$)`), route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(detail()),
  }));
  await page.route(`**/api/tasks/${JOB_ID}/runs**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ runs: [], runnerEvents: [] }),
  }));
  await page.route(`**/api/tasks/${JOB_ID}/output**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '[]',
  }));
  await page.route(`**/api/tasks/${JOB_ID}/session-events**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ events: [], sessionChain: [] }),
  }));
  await page.route(`**/api/tasks/${JOB_ID}/artifacts**`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ jobId: JOB_ID, files: [] }),
  }));
}

test.describe('lane presentation uses one source', () => {
  test.use({ serviceWorkers: 'block', viewport: { width: 1440, height: 900 } });

  for (const theme of ['light', 'dark'] as const) {
    test(`human review name and tone agree across board, header, and Result in ${theme}`, async ({ page }) => {
      await page.addInitScript(selectedTheme => {
        localStorage.setItem('atp.studio.theme', selectedTheme);
      }, theme);
      await installRoutes(page);
      await page.goto('/');

      const boardTitle = page.getByTestId(`lane-title-${LANE_STATE}`);
      const boardColumn = page.getByTestId(`lane-${LANE_STATE}`);
      const boardGlyph = page.getByTestId(`lane-header-avatar-${LANE_STATE}`);
      await expect(boardTitle).toHaveText('Human review');
      const toneToken = await boardColumn.getAttribute('data-lane-tone');
      expect(toneToken).toBe('--studio-lane-human-review');
      const boardTone = await boardGlyph.evaluate(element => getComputedStyle(element).color);

      await page.locator('[data-testid="task-card"]', { hasText: 'One lane identity fixture' }).click();

      const headerChip = page.getByTestId('studio-lane-select');
      await expect(headerChip).toBeVisible();
      await expect(headerChip).toHaveAttribute('data-lane-tone', toneToken!);
      expect(await headerChip.locator('option:checked').textContent()).toBe('Human review');
      expect(await headerChip.evaluate(element => getComputedStyle(element).color)).toBe(boardTone);

      const resultTab = page.getByTestId('inspector-tab-protocol');
      if (await resultTab.getAttribute('aria-selected') !== 'true') await resultTab.click();
      const resultHeader = page.getByTestId('result-case-badge');
      await expect(resultHeader).toContainText('Human review');
      await expect(resultHeader).toHaveAttribute('data-lane-tone', toneToken!);
      expect(await resultHeader.evaluate(element => getComputedStyle(element).color)).toBe(boardTone);
      await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);

      mkdirSync(SHOTS_DIR, { recursive: true });
      await page.screenshot({
        path: join(SHOTS_DIR, `lane-presentation-${theme}--mocked.png`),
        fullPage: false,
      });
    });
  }
});
