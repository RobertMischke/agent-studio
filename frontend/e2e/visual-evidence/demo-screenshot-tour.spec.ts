import { test, expect, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * Screenshot tour of the core app surfaces against the seeded demo workspace
 * (ADR-0056 — `scripts/seed-demo-workspace.mjs`), run against an isolated
 * per-worktree backend/frontend stack (`scripts/worktree-test-stack.sh`
 * pattern) so no private project/task data ever lands in a screenshot.
 *
 * Every capture goes through `shot()`, which attaches the PNG via
 * `testInfo.attach()` so `job-artifact-reporter.ts` harvests it into
 * `<JOB_RESULTS_DIR>/playwright/<spec>/` regardless of local outputDir
 * layout quirks. Filenames carry `--real` (live backend, no route mocks)
 * per the evidence-labelling convention.
 */

test.describe.configure({ mode: 'serial' });
test.use({ viewport: { width: 1600, height: 1000 } });

const TASK_CARD = '[data-testid="task-card"], [data-testid="job-card"]';

async function dismissBlockingOverlays(page: Page): Promise<void> {
  for (let i = 0; i < 40; i++) {
    const dismiss = page.getByTestId('crash-recovery-dismiss').first();
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(350);
      continue;
    }
    if (i < 4) { await page.waitForTimeout(500); continue; }
    break;
  }
  const overlay = page.getByTestId('studio-overlay-root').first();
  if (await overlay.isVisible().catch(() => false)) {
    await page.keyboard.press('Escape').catch(() => undefined);
    await page.waitForTimeout(300);
  }
}

// Project detail is itself rendered inside a studio-overlay-root, so unlike
// dismissBlockingOverlays above this never presses Escape (that would close
// the project view back to the board).
async function dismissCrashRecoveryCards(page: Page): Promise<void> {
  for (let i = 0; i < 20; i++) {
    const card = page.getByTestId('crash-recovery-dismiss').first();
    if (await card.isVisible().catch(() => false)) {
      await card.click({ force: true }).catch(() => undefined);
      await page.waitForTimeout(300);
      continue;
    }
    if (i < 3) { await page.waitForTimeout(400); continue; }
    break;
  }
}

async function shot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const filePath = testInfo.outputPath(`${name}.png`);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  await page.screenshot({ path: filePath, fullPage: false });
  await testInfo.attach(name, { path: filePath, contentType: 'image/png' });
}

/** Capture the current page state in both themes without reloading. */
async function captureBoth(page: Page, testInfo: TestInfo, baseName: string): Promise<void> {
  await setTheme(page, 'dark');
  await page.waitForTimeout(250);
  await shot(page, testInfo, `${baseName}--dark--real`);
  await setTheme(page, 'light');
  await page.waitForTimeout(250);
  await shot(page, testInfo, `${baseName}--light--real`);
}

async function selectProject(page: Page, name: string): Promise<void> {
  const trigger = page.getByTestId('studio-project-picker-trigger');
  await expect(trigger).toBeVisible({ timeout: 15_000 });
  await trigger.click();
  const panel = page.getByTestId('studio-project-picker-panel');
  await expect(panel).toBeVisible({ timeout: 5_000 });
  await panel.getByText(name, { exact: true }).first().click();
  await page.waitForTimeout(500);
}

/**
 * Open the Project Hub via the explorer tree (not a hash deep-link — under
 * `ng serve`'s JIT build the project-detail feature is a lazy chunk and the
 * dev-server compiles it on demand on first hit, which can take well past
 * a hash-reconciliation race). Real clicks tolerate that first-hit compile
 * naturally because we just wait on the resulting DOM.
 */
async function openProjectHub(page: Page, projectName: string): Promise<void> {
  // The parent project row carries an always-visible inline "Open Project
  // Hub" icon button (no separate testid, only an aria-label) — no need to
  // expand the row's children first.
  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible({ timeout: 15_000 });
  await projectRow.getByRole('button', { name: 'Open Project Hub' }).click();
  await dismissCrashRecoveryCards(page);
}

async function clickRail(page: Page, railKey: string, confirmTestId: string): Promise<void> {
  const rail = page.getByTestId(`project-shell-rail-${railKey}`);
  // First hit into project-detail can trigger an on-demand lazy-chunk
  // compile under `ng serve`; generous timeout so a cold first click
  // doesn't flake the capture.
  await expect(rail).toBeVisible({ timeout: 60_000 });
  await rail.click();
  await expect(page.getByTestId(confirmTestId)).toBeVisible({ timeout: 60_000 });
  await page.waitForTimeout(700);
}

const DEMO_APP = 'Demo App';

test('screenshot tour — kanban board (multi-lane)', async ({ page }, testInfo) => {
  await page.goto('/');
  await dismissBlockingOverlays(page);
  await selectProject(page, DEMO_APP);
  await expect(page.locator(TASK_CARD).first()).toBeVisible({ timeout: 15_000 });
  await page.waitForTimeout(500);
  await captureBoth(page, testInfo, 'board-kanban-multi-lane');
});

test('screenshot tour — task detail + activity chat composer', async ({ page }, testInfo) => {
  await page.goto('/');
  await dismissBlockingOverlays(page);
  await selectProject(page, DEMO_APP);

  const card = page.locator(TASK_CARD).filter({ hasText: 'DEMO-5' }).first();
  await expect(card).toBeVisible({ timeout: 15_000 });
  await card.click();
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
  await page.waitForTimeout(400);
  await captureBoth(page, testInfo, 'task-detail-overview');

  const activityTab = page.getByTestId('inspector-tab-activity');
  await expect(activityTab).toBeVisible({ timeout: 10_000 });
  await activityTab.click();
  const compose = page.getByTestId('activity-chat-compose');
  await expect(compose).toBeVisible({ timeout: 10_000 });
  await expect(compose.getByTestId('activity-chat-input')).toBeVisible();
  await expect(compose.getByTestId('activity-chat-send')).toBeVisible();
  await page.waitForTimeout(400);
  await captureBoth(page, testInfo, 'task-detail-activity-chat-composer');
});

test('screenshot tour — orchestrator chat (composer + conversation)', async ({ page }, testInfo) => {
  await page.goto('/');
  await dismissBlockingOverlays(page);
  await selectProject(page, DEMO_APP);

  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  const sheet = page.getByTestId('orch-side-sheet');
  await expect(sheet).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('conversation-view')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('chat-input')).toBeVisible();
  await page.waitForTimeout(1_000);
  await captureBoth(page, testInfo, 'orchestrator-chat-conversation');

  await page.getByTestId('orch-context-badge').click();
  await expect(page.getByTestId('orch-context-menu')).toBeVisible();
  await captureBoth(page, testInfo, 'orchestrator-chat-context-menu');
});

test('screenshot tour — project hub rails (URLs, token usage, wiki)', async ({ page }, testInfo) => {
  test.setTimeout(180_000);
  await page.goto('/');
  await dismissBlockingOverlays(page);
  await openProjectHub(page, DEMO_APP);

  await clickRail(page, 'project-urls', 'project-urls-panel');
  await captureBoth(page, testInfo, 'project-settings-urls-panel');

  await clickRail(page, 'token-usage', 'project-token-usage-panel');
  await captureBoth(page, testInfo, 'statistics-token-usage');

  await clickRail(page, 'wiki', 'project-shell-panel-wiki');
  const tree = page.getByTestId('project-wiki-tree');
  if (await tree.isVisible().catch(() => false)) {
    const firstFile = tree.locator('[data-testid^="project-wiki-file-"]').first();
    if (await firstFile.count()) {
      await firstFile.click().catch(() => undefined);
      await page.getByTestId('project-wiki-viewer').first().waitFor({ state: 'visible', timeout: 8_000 }).catch(() => undefined);
      await page.waitForTimeout(400);
    }
  }
  await captureBoth(page, testInfo, 'wiki-view');
});
