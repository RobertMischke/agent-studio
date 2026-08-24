import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-2677: a project whose build-profile gate is shut must say so on the
 * project itself. The incident this guards is a silent gate - 25 Ready cards
 * that no runner could claim for five days, with nothing on screen to say why.
 *
 * Fully mocked: this spec needs the frontend dev server only.
 */
const PROJECT_ID = 'PROJ-2677';
const PROJECT_NAME = 'Quality Studio';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'test-results', 'project-pickup-blocked-banner');

const project = {
  id: PROJECT_ID,
  displayName: PROJECT_NAME,
  shortCode: 'QS',
  workspaceId: 'WS-2677',
  storageLocation: '/mock/tasks/quality-studio',
  rootPath: '/mock/repos/quality-studio',
  repositoryPath: '/mock/repos/quality-studio',
  sortOrder: 0,
  archived: false,
  urls: [],
};

async function json(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** The gate payload under test; each case overrides what it cares about. */
type GateBody = Record<string, unknown>;

async function installRoutes(page: Page, gate: GateBody): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'WS-2677',
    displayName: 'Fleet Workspace',
    sortOrder: 0,
    isDefault: true,
    projects: [project],
  }]));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: PROJECT_NAME,
    path: project.storageLocation,
    rootPath: project.rootPath,
  }]));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  }));
  await page.route(/\/api\/runner\/status(?:\?|$)/, route => json(route, { projects: {} }));
  await page.route(/\/api\/projects\/[^/]+\/snapshot(?:\?|$)/, route => json(route, {
    project: PROJECT_NAME,
    capturedAt: '2026-08-23T12:00:00Z',
    paths: {
      path: project.storageLocation,
      rootPath: project.rootPath,
      repositoryPath: project.repositoryPath,
    },
    settings: {
      autoCommit: true,
      crashRecoveryEnabled: true,
      autoPushStrategy: 'on-completed',
      runnerMode: 'auto-continuous',
      orchestratorModel: null,
    },
    runnerStatus: null,
    orchestratorLogTail: [],
    orchestratorSession: null,
    reviewDecisionsPending: [],
    runnerPendingDecisions: [],
    publishTargets: [],
    queueHealth: {
      severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [],
    },
  }));
  await page.route(/\/api\/projects\/Quality%20Studio\/build-profile(?:\?|$)/, route => json(route, {
    profile: { stack: 'dotnet', buildCmds: ['dotnet build QualityStudio.slnx'] },
    plannedDryRun: [{ kind: 'Build', command: 'dotnet build QualityStudio.slnx' }],
    gateApplicable: true,
    verifyPlan: { source: 'build-profile', commands: [] },
    ...gate,
  }));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(?:\?|$)/, route => json(route, {
    projectName: PROJECT_NAME, items: [], patterns: [],
  }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-23T12:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-23T12:00:00Z', sessions: [],
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null,
  }));
}

async function openSettings(page: Page, gate: GateBody): Promise<void> {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await installRoutes(page, gate);
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.removeItem('atp.studio.tabs.v1');
  });
  await page.goto(`/#/projects/${PROJECT_ID}/settings`);
  await expect(page.getByTestId('project-shell-panel-settings')).toBeVisible({ timeout: 20_000 });
}

test('a shut build-profile gate names the ready cards it is holding back', async ({ page }, testInfo) => {
  await openSettings(page, {
    status: 'declared',
    pickupAllowed: false,
    gateReason: 'build profile declared but not yet validated (no green dry-run)',
    gateReasonCode: 'not-validated',
    readyCardCount: 25,
    validationWorkspace: '/mock/repos/quality-studio',
    revalidationRunsRemaining: null,
    lastRemoteVerification: null,
    remoteVerificationCurrent: false,
  });

  const banner = page.getByTestId('project-pickup-blocked-banner');
  await expect(banner).toBeVisible();
  await expect(banner).toContainText('25 ready cards are not claimable: build profile not validated');
  await expect(banner).toContainText(
    'Gate reason: build profile declared but not yet validated (no green dry-run)');
  await expect(banner).toContainText('/mock/repos/quality-studio');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const shot = path.join(RESULTS_DIR, `agt-2677--project-pickup-blocked-banner--${theme}.png`);
    await banner.screenshot({ path: shot });
    await testInfo.attach(`project-pickup-blocked-banner--${theme}`, {
      path: shot,
      contentType: 'image/png',
    });
  }
});

test('the revalidation grace is disclosed instead of looking healthy', async ({ page }, testInfo) => {
  await openSettings(page, {
    status: 'revalidation-pending',
    pickupAllowed: true,
    gateReason: 'build profile edited after a green validation; revalidation pending (2 run(s) of grace left)',
    gateReasonCode: 'revalidation-pending',
    readyCardCount: 0,
    validationWorkspace: '/mock/repos/quality-studio',
    revalidationRunsRemaining: 2,
    lastRemoteVerification: null,
    remoteVerificationCurrent: false,
  });

  await expect(page.getByTestId('project-pickup-blocked-banner')).toHaveCount(0);
  const banner = page.getByTestId('project-pickup-revalidation-banner');
  await expect(banner).toBeVisible();
  await expect(banner).toContainText('Build profile edited: revalidation pending');
  await expect(banner).toContainText('2 more runs');

  const shot = path.join(RESULTS_DIR, 'agt-2677--project-pickup-revalidation-banner.png');
  await banner.screenshot({ path: shot });
  await testInfo.attach('project-pickup-revalidation-banner', {
    path: shot,
    contentType: 'image/png',
  });
});

test('a proven profile leaves the settings page quiet', async ({ page }) => {
  await openSettings(page, {
    status: 'pipeline-ready',
    pickupAllowed: true,
    gateReason: 'pipeline-ready',
    gateReasonCode: 'pipeline-ready',
    readyCardCount: 0,
    validationWorkspace: '/mock/repos/quality-studio',
    revalidationRunsRemaining: null,
    lastRemoteVerification: null,
    remoteVerificationCurrent: false,
  });

  await expect(page.getByTestId('project-pickup-blocked-banner')).toHaveCount(0);
  await expect(page.getByTestId('project-pickup-revalidation-banner')).toHaveCount(0);
});
