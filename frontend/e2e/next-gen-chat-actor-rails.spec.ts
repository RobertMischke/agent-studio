import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

const FLAG_KEY = 'atp.flag.nextGenChatPrototype';
const evidenceDir = path.resolve(__dirname, '../../docs/mockups/chat-window-next-gen/evidence');

async function enablePrototype(page: Page): Promise<void> {
  await page.addInitScript((key) => {
    localStorage.setItem(key, '1');
  }, FLAG_KEY);
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
        json: { preparation: [], ready: [], progress: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [] }
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

const ACTOR_KINDS = ['user', 'agent', 'orchestrator', 'supervisor', 'support', 'tool', 'system'] as const;
const DECISION_KINDS = ['reissue', 'heuristic', 'needsInput', 'circuit', 'captureFail', 'drift'] as const;
const INTERVENTION_TARGETS = ['currentRun', 'nextRun', 'orchestrator', 'followUp'] as const;

test.describe('@mockup next-gen chat actor rails and decision cards', () => {
  test('renders actor rails, decision cards, and target-aware user interventions', async ({ page }) => {
    await stubApi(page);
    await enablePrototype(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.addStyleTag({
      content: '.dev-banner{display:none!important}body{padding-top:0!important}',
    });

    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toBeVisible();

    // 1. Actor rail surfaces every actor identity, with non-color cues (icon + glyph + label).
    const actorKey = page.getByTestId('prototype-actor-key');
    await expect(actorKey).toBeVisible();
    for (const kind of ACTOR_KINDS) {
      const chip = page.getByTestId(`prototype-actor-chip-${kind}`);
      await expect(chip).toBeVisible();
      // Each chip carries a shape attribute on its avatar so identity is non-color too.
      await expect(chip.locator('.actor-avatar')).toHaveAttribute('data-shape', /.+/);
    }

    // 2. Default scenario already shows an orchestrator reissue decision row.
    const reissue = page.getByTestId('prototype-decision-reissue');
    await expect(reissue).toBeVisible();
    await expect(reissue).toHaveAttribute('data-actor', 'orchestrator');
    await expect(reissue).toContainText('Reissue');
    await expect(reissue).toContainText('used 1 of 1 reissues');

    // 3. Compact row stays compact until expanded; expanded form preserves causality.
    await expect(page.getByTestId('prototype-decision-detail-reissue')).toHaveCount(0);
    await reissue.locator('.decision__row').click();
    const reissueDetail = page.getByTestId('prototype-decision-detail-reissue');
    await expect(reissueDetail).toBeVisible();
    for (const label of ['Reason', 'Evidence', 'Action', 'Retry budget', 'Token usage', 'Next step']) {
      await expect(reissueDetail).toContainText(label);
    }
    await expect(reissueDetail).toContainText('lines 318-431');

    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-actor-rails-default.png'),
      fullPage: false,
    });

    // 4. The Decisions scenario surfaces every required decision row.
    await page.getByTestId('prototype-scenario-decisions').click();
    for (const decision of DECISION_KINDS) {
      await expect(page.getByTestId(`prototype-decision-${decision}`)).toBeVisible();
    }

    // 5. Target-aware user interventions are visible and distinct.
    for (const target of INTERVENTION_TARGETS) {
      await expect(page.getByTestId(`prototype-target-${target}`).first()).toBeVisible();
    }

    // 6. Supervisor surfaces with a non-orchestrator actor color so it is distinguishable from agent prose.
    const circuit = page.getByTestId('prototype-decision-circuit');
    await expect(circuit).toHaveAttribute('data-actor', 'supervisor');
    await circuit.locator('.decision__row').click();
    await expect(page.getByTestId('prototype-decision-detail-circuit')).toContainText('Operator can pre-empt');

    // 7. Capture light + dark evidence with a needs-input decision expanded.
    await page.getByTestId('prototype-decision-needsInput').locator('.decision__row').click();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-actor-rails-decisions-light.png'),
      fullPage: false,
    });

    await page.getByTestId('prototype-theme-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-theme', 'dark');
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-actor-rails-decisions-dark.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-theme-toggle').click();

    // 8. Compact density keeps decision rows compact and actor rail still legible.
    await page.getByTestId('prototype-density-toggle').click();
    await expect(page.getByTestId('next-gen-chat-angular-prototype')).toHaveAttribute('data-density', 'compact');
    await expect(page.getByTestId('prototype-actor-key')).toBeVisible();
    await expect(page.getByTestId('prototype-decision-needsInput')).toBeVisible();
    await page.screenshot({
      path: path.join(evidenceDir, 'next-gen-chat-actor-rails-decisions-compact.png'),
      fullPage: false,
    });
    await page.getByTestId('prototype-density-toggle').click();
  });
});
