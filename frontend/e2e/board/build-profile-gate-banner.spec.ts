import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * AGT-2677. The 2026-08-18 Quality Studio outage was invisible, not obscure: 25
 * ready cards were unclaimable for five days because the project's build profile
 * had reset to `declared`, and every surface stayed quiet about it. This spec
 * proves the three surfaces that now speak - the workspace banner, the card's
 * durable rejection, and the fact that the gate reason is attributed to the
 * project rather than to a runner.
 */
const PROJECT = 'QualityStudio';
const WATCH_PATH = '/fixtures/quality-studio';
const RESULTS = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : join(process.cwd(), '..', 'results');

const GATE_REASON =
  'build profile declared but not yet validated (no green dry-run and no green run on the assigned runner)';

function gatedCard(index: number) {
  const id = `qs-${index}`;
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key: `QS-${index}`,
    title: `Quality Studio card ${index}`,
    state: '2-ready',
    order: index,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-08-18T06:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${id}`,
    lastActivity: '2026-08-18T06:00:00Z',
    sessionName: `session-${id}`,
    model: 'gpt-5.6-codex',
    useOwnSession: null,
    lastUsage: null,
    commit: null,
    ownerClientId: 'local-default',
    tags: [],
    executionLocation: {
      state: 'queued-remote',
      executionKind: 'remote',
      runnerId: 'agent-runner-01',
      clientId: 'agent-runner-01',
      hostDisplayName: 'agent-runner-01',
      configuredRunnerId: 'agent-runner-01',
      startedAt: null,
      lastHeartbeat: null,
      lastActivityAt: null,
      processId: null,
      sessionId: null,
      branch: null,
      worktreePath: null,
      connectionState: 'queued',
      leaseState: 'none',
      trustReason: 'Project routing queues this task for the configured remote runner.',
      historical: false,
      lastRejection: {
        code: 'build-profile-gate',
        runnerId: 'agent-runner-01',
        runnerName: 'agent-runner-01',
        reason: GATE_REASON,
        rejectedAtUtc: '2026-08-23T21:00:00Z',
      },
    },
  };
}

const gatedCards = [gatedCard(92), gatedCard(79), gatedCard(68)];

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function expectFlatFullBleedNoticeBar(banner: Locator): Promise<void> {
  const geometry = await banner.evaluate(element => {
    const workspaceBanner = element.closest('app-workspace-banner');
    if (!workspaceBanner) throw new Error('Notice-bar shell is missing.');
    const rect = element.getBoundingClientRect();
    const workspaceRect = workspaceBanner.getBoundingClientRect();
    const style = getComputedStyle(element);
    return {
      left: rect.left,
      right: rect.right,
      workspaceLeft: workspaceRect.left,
      workspaceRight: workspaceRect.right,
      borderRadius: style.borderRadius,
      boxShadow: style.boxShadow,
      borderLeftWidth: style.borderLeftWidth,
    };
  });

  expect(geometry.left).toBeCloseTo(geometry.workspaceLeft, 0);
  expect(geometry.right).toBeCloseTo(geometry.workspaceRight, 0);
  expect(geometry.borderRadius).toBe('0px');
  expect(geometry.boxShadow).toBe('none');
  // Design hard rule: status is carried by tint and glyph, never a left accent bar.
  expect(geometry.borderLeftWidth).toBe('0px');
}

async function installRoutes(page: Page, gateBlocked: boolean): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (url.includes('/api/auth/status')) return json(route, {
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    });
    if (url.includes('/api/tasks/grouped')) return json(route, {
      backlog: [], preparation: [], orchestratorPrep: [],
      ready: gatedCards, progress: [], failedPickup: [],
      codeNotComplete: [], review: [], autoReview: [],
      humanReview: [], escalated: [], completed: [], archive: [],
    });
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, gatedCards);
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/clients')) return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/runner/queue-starvation')) return json(route, {
      active: gateBlocked,
      waitingTaskCount: gateBlocked ? gatedCards.length : 0,
      availableSlots: 8,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: '2026-08-23T20:59:00Z',
      hasRejections: gateBlocked,
      oldestEnteredLaneAt: gateBlocked ? '2026-08-18T06:00:00Z' : null,
      observedAt: '2026-08-23T21:05:00Z',
      items: gateBlocked
        ? gatedCards.map(card => ({
            taskId: card.id,
            taskKey: card.taskKey,
            projectName: card.projectName,
            title: card.title,
            enteredLaneAt: '2026-08-18T06:00:00Z',
            lastRejection: card.executionLocation.lastRejection,
            buildProfileGateBlocked: true,
          }))
        : [],
      gateBlockedTaskCount: gateBlocked ? gatedCards.length : 0,
      gateBlockedProjects: gateBlocked
        ? [{
            projectName: PROJECT,
            readyTaskCount: gatedCards.length,
            gateCode: 'not-validated',
            gateReason: GATE_REASON,
            buildProfileStatus: 'declared',
          }]
        : [],
    });
    if (url.includes('/api/pipeline/accepted-integration-alert')) return json(route, {
      active: false, stalledTaskCount: 0, thresholdMinutes: 30,
      observedAt: '2026-08-23T21:05:00Z', oldestAcceptedAt: null, items: [],
    });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

async function openBoard(page: Page, gateBlocked: boolean): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page, gateBlocked);
  await page.goto('/?includeFixtures=true');
  await page.addStyleTag({ content: '.dialog__overlay { display: none !important; }' });
}

test('names the closed build-profile gate on the banner and on every held card', async ({ page }) => {
  mkdirSync(RESULTS, { recursive: true });
  await page.setViewportSize({ width: 1280, height: 900 });
  await openBoard(page, true);

  const banner = page.getByTestId('build-profile-gate-banner');
  await expect(banner).toContainText('3 ready cards are not claimable: build profile not validated.');
  await expect(banner).toContainText('QualityStudio (3)');
  await expect(banner).toContainText('Re-run the build-profile validation in project settings');
  await expectFlatFullBleedNoticeBar(banner);

  // The card must name the same cause, and must not blame the runner for a
  // project setting - that misattribution is what sent the operator to restart
  // hosts during the outage.
  const card = page.getByTestId('task-card').filter({ hasText: 'Quality Studio card 92' });
  const rejection = card.getByTestId('remote-dispatch-rejection');
  await expect(rejection).toContainText('Project build profile not validated:');
  await expect(rejection).not.toContainText('Runner agent-runner-01 rejected');
  await expect(rejection).toContainText('not yet validated');

  // The generic queue alarm must not double-report the same cards.
  await expect(page.getByTestId('remote-queue-starvation-banner')).toHaveCount(0);

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await expect(banner).toBeVisible();
    await page.screenshot({
      path: join(RESULTS, `build-profile-gate-banner-${theme}--mocked.png`),
      fullPage: false,
    });
  }
});

test('stays silent once the build profile is validated again', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await openBoard(page, false);

  await expect(page.getByTestId('task-card').first()).toBeVisible();
  await expect(page.getByTestId('build-profile-gate-banner')).toHaveCount(0);
});
