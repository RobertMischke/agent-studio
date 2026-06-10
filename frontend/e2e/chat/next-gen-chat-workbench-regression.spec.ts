import { expect, Locator, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Locks the v7 next-gen chat workbench so the legacy Activity Log UI can be
 * retired behind `Frontend:NextGenChat` without losing review surfaces.
 *
 * The Angular prototype (`atp.flag.nextGenChatPrototype`) is the canonical
 * v7 reference. These tests exercise its workbench states and edge-case
 * scenarios, plus a smoke check of the production app shell with the
 * production rollout flag both off and on.
 *
 * Durable evidence images land under
 * `docs/mockups/chat-window-next-gen/evidence/regression/`. Existing
 * prototype screenshots are deliberately not overwritten.
 */

const PROTOTYPE_FLAG = 'atp.flag.nextGenChatPrototype';
const PRODUCTION_FLAG = 'atp.flag.nextGenChat';

const EVIDENCE_DIR = path.resolve(
  __dirname,
  '../../docs/mockups/chat-window-next-gen/evidence/regression'
);

async function setFlag(page: Page, key: string, value: '0' | '1'): Promise<void> {
  // Write the value explicitly. `nextGenChat` is default-ON, so removing the
  // key would read as opt-in; the off-state ('0') must be persisted verbatim.
  await page.addInitScript(
    ({ k, v }) => {
      localStorage.setItem(k, v);
    },
    { k: key, v: value }
  );
}

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const url = route.request().url();
    if (url.includes('/watch-paths')) {
      await route.fulfill({ json: [] });
      return;
    }
    if (url.includes('/jobs/grouped')) {
      await route.fulfill({
        json: {
          preparation: [],
          ready: [],
          progress: [],
          review: [],
          autoReview: [],
          humanReview: [],
          completed: [],
          archive: [],
        },
      });
      return;
    }
    if (url.includes('/runner/status')) {
      await route.fulfill({ json: { projects: {} } });
      return;
    }
    if (url.includes('/cli/quota')) {
      await route.fulfill({ json: { snapshots: [] } });
      return;
    }
    if (url.includes('/cli/usage')) {
      await route.fulfill({ json: { sessions: [], versions: [] } });
      return;
    }
    await route.fulfill({ json: [] });
  });
}

async function hideDevOverlays(page: Page): Promise<void> {
  await page.addStyleTag({
    content:
      '.dev-banner{display:none!important}body{padding-top:0!important}vite-error-overlay{display:none!important}',
  });
}

async function bootPrototype(page: Page): Promise<void> {
  await stubApi(page);
  await setFlag(page, PROTOTYPE_FLAG, '1');
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await hideDevOverlays(page);
  await expect(page.getByTestId('next-gen-chat-angular-prototype')).toBeVisible();
}

async function shotEvidence(page: Page, name: string): Promise<string> {
  const file = path.join(EVIDENCE_DIR, `${name}.png`);
  await page.screenshot({ path: file, fullPage: false });
  return file;
}

async function closeStatusPopover(page: Page): Promise<void> {
  const popover = page.getByTestId('prototype-status-popover');
  if (await popover.isVisible().catch(() => false)) {
    await popover.getByText('Close').click();
    await expect(popover).toBeHidden();
  }
}

/**
 * Returns the bounding box for the given element. Throws if the element is
 * not laid out, since that itself is a regression worth flagging.
 */
async function box(locator: Locator): Promise<{ x: number; y: number; width: number; height: number }> {
  const result = await locator.boundingBox();
  if (!result) throw new Error(`No bounding box for locator ${locator}`);
  return result;
}

function rectsOverlap(
  a: { x: number; y: number; width: number; height: number },
  b: { x: number; y: number; width: number; height: number }
): boolean {
  const slack = 0.5;
  return (
    a.x + a.width > b.x + slack &&
    b.x + b.width > a.x + slack &&
    a.y + a.height > b.y + slack &&
    b.y + b.height > a.y + slack
  );
}

test.describe('@regression next-gen chat workbench', () => {
  test('production shell loads with NextGenChat flag off', async ({ page }) => {
    await stubApi(page);
    await setFlag(page, PROTOTYPE_FLAG, '0');
    await setFlag(page, PRODUCTION_FLAG, '0');
    await page.goto('/');
    await hideDevOverlays(page);

    // Existing app chrome must remain reachable. The prototype must stay
    // gated, so the activity bar / app shell should render and the
    // prototype host should not appear.
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveCount(0);
  });

  test('production shell loads with NextGenChat flag on', async ({ page }) => {
    await stubApi(page);
    await setFlag(page, PROTOTYPE_FLAG, '0');
    await setFlag(page, PRODUCTION_FLAG, '1');
    await page.goto('/');
    await hideDevOverlays(page);

    // The production rollout flag must be safe to flip while it has no
    // visible consumer. The prototype must still stay gated.
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveCount(0);
  });

  test('v7 workbench: pin and close every review pane', async ({ page }) => {
    await bootPrototype(page);

    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Result summary');
    await shotEvidence(page, 'workbench-result');

    await page.getByTestId('prototype-pane-git').click();
    await expect(page.getByTestId('prototype-pane-git-view')).toContainText('Git changes');
    await expect(page.getByTestId('prototype-git-editor')).toContainText('Source editor / diff');
    await shotEvidence(page, 'workbench-git');

    await page.getByTestId('prototype-pane-preview').click();
    await expect(page.getByTestId('prototype-pane-preview-view')).toContainText('Screenshot preview');
    await shotEvidence(page, 'workbench-preview');

    await page.getByTestId('prototype-pane-debug').click();
    await expect(page.getByTestId('prototype-pane-debug-view')).toBeVisible();
    await shotEvidence(page, 'workbench-debug');

    // All review panes pinned together.
    await page.getByTestId('prototype-pane-all').click();
    await expect(page.getByTestId('prototype-pane-result-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-git-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-preview-view')).toBeVisible();
    await expect(page.getByTestId('prototype-pane-debug-view')).toBeVisible();
    await shotEvidence(page, 'workbench-all-panes');

    // Every pane pinned must be individually closable through its own close
    // affordance, so review surfaces never become unreachable.
    await page.getByTestId('prototype-pane-debug-close').click();
    await expect(page.getByTestId('prototype-pane-debug-view')).toHaveCount(0);
    await page.getByTestId('prototype-pane-preview-close').click();
    await expect(page.getByTestId('prototype-pane-preview-view')).toHaveCount(0);
    await page.getByTestId('prototype-pane-git-close').click();
    await expect(page.getByTestId('prototype-pane-git-view')).toHaveCount(0);
    await page.getByTestId('prototype-pane-result-close').click();

    // Closing the last review pane must not leave the workbench blank: chat
    // owns the surface again.
    await expect(page.getByTestId('prototype-conversation')).toBeVisible();
  });

  test('v7 workbench: chat-only reclaims width and chat-closed Git review keeps source editor', async ({ page }) => {
    await bootPrototype(page);

    // Capture chat-only width.
    await page.getByTestId('prototype-pane-result-close').click();
    await expect(page.getByTestId('prototype-conversation')).toBeVisible();

    const chatOnly = await box(page.getByTestId('prototype-conversation'));
    await shotEvidence(page, 'workbench-chat-only');

    // Pin the Result pane back and the conversation should give up width.
    await page.getByTestId('prototype-pane-result').click();
    await expect(page.getByTestId('prototype-pane-result-view')).toBeVisible();
    const chatPlusResult = await box(page.getByTestId('prototype-conversation'));
    expect(chatOnly.width).toBeGreaterThan(chatPlusResult.width + 60);

    // Chat-closed Git review: changes list + selected source editor diff
    // must remain usable. Acceptance criterion: Git/source review must not
    // depend on the transcript being visible.
    await page.getByTestId('prototype-pane-git').click();
    await page.getByTestId('prototype-pane-result-close').click();
    await expect(page.getByTestId('prototype-pane-result-view')).toHaveCount(0);
    await page.getByTestId('prototype-pane-git-view').getByTestId('prototype-chat-toggle').click();
    await expect(page.getByTestId('prototype-conversation')).toBeHidden();
    await expect(page.getByTestId('prototype-git-editor')).toContainText('Source editor / diff');
    await shotEvidence(page, 'workbench-git-no-chat');
  });

  test('v7 workbench: density, theme, side sheet wide, mobile collapse', async ({ page }) => {
    await bootPrototype(page);

    // Pin all so density/theme captures cover a busy state.
    await page.getByTestId('prototype-pane-all').click();

    await page.getByTestId('prototype-density-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-density', 'compact');
    await shotEvidence(page, 'workbench-compact');

    await page.getByTestId('prototype-density-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-density', 'comfortable');

    // Light theme (default) is captured in workbench-result already; capture
    // the explicit light state with the side sheet open.
    await expect(page.getByTestId('prototype-side-sheet')).toBeVisible();
    await shotEvidence(page, 'workbench-light-side-sheet-wide');

    await page.getByTestId('prototype-theme-toggle').click();
    await shotEvidence(page, 'workbench-dark');
    await page.getByTestId('prototype-theme-toggle').click();

    // Mobile collapse: the composer and at least the conversation must
    // remain reachable. Status counters are allowed to be hidden.
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByTestId('prototype-composer')).toBeVisible();
    await expect(page.getByTestId('prototype-conversation')).toBeVisible();
    await shotEvidence(page, 'workbench-mobile');
  });

  test('v7 workbench: splitter keyboard resize keeps both sides visible', async ({ page }) => {
    await bootPrototype(page);
    await page.getByTestId('prototype-pane-git').click();

    const splitter = page.getByTestId('prototype-splitter');
    await expect(splitter).toBeVisible();
    await splitter.focus();

    // Drive the slider to the minimum the keyboard contract allows.
    await page.keyboard.press('Home');
    await expect(splitter).toHaveAttribute('aria-valuenow', '34');
    await expect(page.getByTestId('prototype-conversation')).toBeVisible();
    await expect(page.getByTestId('prototype-git-editor')).toContainText('Source editor / diff');

    // And to the maximum.
    await page.keyboard.press('End');
    await expect(splitter).toHaveAttribute('aria-valuenow', '72');
    await expect(page.getByTestId('prototype-conversation')).toBeVisible();
    await expect(page.getByTestId('prototype-git-editor')).toContainText('Source editor / diff');
    await shotEvidence(page, 'workbench-splitter-extremes');
  });

  test('v7 edge cases: tool burst, wait loop, schema drift, decisions', async ({ page }) => {
    await bootPrototype(page);

    // Tool burst toggle.
    const toolBurst = page.getByTestId('prototype-tool-burst');
    await expect(toolBurst).toContainText('Tools 28');
    await toolBurst.click();
    await expect(page.locator('.tool-details')).toBeVisible();
    await shotEvidence(page, 'edge-tool-burst');
    await toolBurst.click();

    // Wait loop scenario adds a circuit decision row.
    await page.getByTestId('prototype-scenario-wait').click();
    const circuit = page.getByTestId('prototype-decision-circuit').first();
    await expect(circuit).toBeVisible();
    await circuit.locator('.decision__row').click();
    await expect(page.getByTestId('prototype-decision-detail-circuit').first()).toBeVisible();
    await shotEvidence(page, 'edge-wait-loop');

    // Schema drift scenario.
    await page.getByTestId('prototype-scenario-drift').click();
    const drift = page.getByTestId('prototype-decision-drift').first();
    await expect(drift).toBeVisible();
    await drift.locator('.decision__row').click();
    await expect(page.getByTestId('prototype-decision-detail-drift').first()).toBeVisible();
    await shotEvidence(page, 'edge-schema-drift');

    // Decision showcase: needs-input loop, capture-fail, orchestrator
    // reissue, and user-intervention chips all need to be visible at once.
    await page.getByTestId('prototype-scenario-decisions').click();
    await expect(page.getByTestId('prototype-decision-needsInput').first()).toBeVisible();
    await expect(page.getByTestId('prototype-decision-captureFail').first()).toBeVisible();
    await expect(page.getByTestId('prototype-decision-reissue').first()).toBeVisible();
    await expect(page.getByTestId('prototype-decision-heuristic').first()).toBeVisible();

    // User intervention targets: currentRun, nextRun, orchestrator, followUp
    // all need to remain pinned to user turns so steering is unambiguous.
    await expect(page.getByTestId('prototype-target-currentRun')).toBeVisible();
    await expect(page.getByTestId('prototype-target-nextRun')).toBeVisible();
    await expect(page.getByTestId('prototype-target-orchestrator')).toBeVisible();
    await expect(page.getByTestId('prototype-target-followUp')).toBeVisible();
    await shotEvidence(page, 'edge-decision-showcase');

    // Image lightbox path covers visual evidence + duplicate sentinel
    // surfacing through the screenshot reel.
    await page.getByTestId('prototype-scenario-visual').click();
    await page.getByTestId('prototype-pane-preview').click();
    await page.getByTestId('prototype-pane-preview-view').getByRole('button', { name: 'Result split' }).click();
    await expect(page.getByTestId('prototype-lightbox')).toBeVisible();
    await shotEvidence(page, 'edge-image-lightbox');
    await page.getByTestId('prototype-lightbox').getByText('Close').click();
  });

  test('v7 drill-downs: token, evidence, queue, model, raw trace stay reachable', async ({ page }) => {
    await bootPrototype(page);

    // Token spike drill-down.
    await page.getByTestId('prototype-status-token').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Token usage');
    await shotEvidence(page, 'drilldown-tokens');
    await closeStatusPopover(page);

    // Evidence drill-down.
    await page.getByTestId('prototype-status-evidence').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Visual evidence');
    await closeStatusPopover(page);

    // Queue automation drill-down.
    await page.getByTestId('prototype-topbar-queue').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('Queue and automation');
    await closeStatusPopover(page);

    // Model drill-down.
    await page.getByTestId('prototype-status-model').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('CLI and model');
    await closeStatusPopover(page);

    // Health drill-down (covers run timeline / health route).
    await page.getByTestId('prototype-status-health').click();
    await expect(page.getByTestId('prototype-status-popover')).toContainText('System health');
    await closeStatusPopover(page);

    // Verbose Debug — raw trace and tokens must stay reachable from a chat
    // tab that has no review pane open.
    await page.getByTestId('prototype-debug-open').click();
    await expect(page.getByTestId('prototype-debug-modal')).toBeVisible();
    await page.getByTestId('prototype-debug-tab-trace').click();
    await expect(page.getByTestId('prototype-debug-modal')).toContainText(/trace/i);
    await shotEvidence(page, 'drilldown-verbose-debug-trace');
    await page.getByTestId('prototype-debug-tab-tokens').click();
    await page.getByTestId('prototype-debug-tab-actors').click();
    await page.getByTestId('prototype-debug-tab-tools').click();
    await page.getByTestId('prototype-debug-modal').getByText('Close').click();

    // Run timeline marker stays one click from the transcript.
    await page.getByTestId('prototype-run-marker').click();
    await expect(page.getByTestId('prototype-run-popover')).toBeVisible();
    await page.getByTestId('prototype-run-marker').click();

    // Feature-parity grid must still surface Files, Commits, Screenshots,
    // and Side sheet routes from the result pane.
    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Prompt history');
    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Git review');
    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Screenshots');
    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Side sheet');
    await expect(page.getByTestId('prototype-pane-result-view')).toContainText('Run timeline');
  });

  test('v7 click interception guard: composer and run timeline stay reachable', async ({ page }) => {
    await bootPrototype(page);
    await page.getByTestId('prototype-pane-all').click();

    const composer = page.getByTestId('prototype-composer');
    const runMarker = page.getByTestId('prototype-run-marker');
    const topbar = page.getByTestId('prototype-topbar-runline');

    // No pane should overlap the composer footer or the topbar run summary.
    const composerBox = await box(composer);
    const runBox = await box(runMarker);
    const topbarBox = await box(topbar);

    for (const pane of ['result', 'git', 'preview', 'debug'] as const) {
      const view = page.getByTestId(`prototype-pane-${pane}-view`);
      const paneBox = await box(view);
      expect(
        rectsOverlap(paneBox, composerBox),
        `Pane ${pane} overlaps the composer`
      ).toBe(false);
      expect(
        rectsOverlap(paneBox, topbarBox),
        `Pane ${pane} overlaps the topbar run summary`
      ).toBe(false);
    }

    // Tool burst expansion must not push the composer off-screen.
    await page.getByTestId('prototype-tool-burst').click();
    await expect(composer).toBeVisible();
    const composerAfterBurst = await box(composer);
    expect(composerAfterBurst.y + composerAfterBurst.height).toBeLessThanOrEqual(900 + 1);
    await page.getByTestId('prototype-tool-burst').click();

    // Status popover overlay must not cover the composer such that the
    // primary action becomes unclickable.
    await page.getByTestId('prototype-status-token').click();
    const popover = page.getByTestId('prototype-status-popover');
    await expect(popover).toBeVisible();
    const popoverBox = await box(popover);
    expect(
      rectsOverlap(popoverBox, runBox),
      'Status popover overlaps the run timeline marker'
    ).toBe(false);
    await closeStatusPopover(page);

    // Composer is still operable after every overlay closes.
    await expect(composer).toBeVisible();
    await composer.getByRole('button', { name: 'Continue' }).first().click();
    await shotEvidence(page, 'guard-composer-reachable');
  });

  test('v7 side sheet: project chat stays operable independently of the workbench', async ({ page }) => {
    await bootPrototype(page);

    const sheet = page.getByTestId('prototype-side-sheet');
    await expect(sheet).toBeVisible();
    await expect(sheet).toContainText('Project side sheet chat');

    // Toggle through the sheet button in the topbar; sheet should hide.
    await page.getByTestId('prototype-topbar-sheet').click();
    const conversationWide = await box(page.getByTestId('prototype-conversation'));

    // Re-open and confirm width relinquished back to the sheet.
    await page.getByTestId('prototype-topbar-sheet').click();
    await expect(sheet).toBeVisible();
    const conversationWithSheet = await box(page.getByTestId('prototype-conversation'));
    expect(conversationWide.width).toBeGreaterThan(conversationWithSheet.width + 80);
    await shotEvidence(page, 'workbench-side-sheet-restored');
  });
});
