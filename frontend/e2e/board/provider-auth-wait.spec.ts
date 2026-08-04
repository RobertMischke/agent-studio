import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Provider auth wait';
const WATCH_PATH = '/fixtures/provider-auth-wait';
const SHOT_DIR = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');

const readyTask = {
  id: 'auth-wait-ready', taskKey: `${WATCH_PATH}::auth-wait-ready`, key: 'AUTH-1',
  title: 'Implement with Claude', state: '2-ready', order: 1, agent: 'claude', cliType: 'claude',
  createdAt: '2026-08-04T08:00:00Z', watchPath: WATCH_PATH, projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/auth-wait-ready`, lastActivity: '2026-08-04T09:00:00Z',
  sessionName: null, model: 'claude-opus-4-8', useOwnSession: null, commit: null,
  ownerClientId: 'agent-runner-01', tags: [],
  executionLocation: {
    state: 'queued-remote', executionKind: 'remote', runnerId: null, clientId: null,
    hostDisplayName: 'agent-runner-01', configuredRunnerId: 'agent-runner-01',
    connectionState: 'queued', leaseState: 'none',
    trustReason: 'The project is assigned to this remote runner.',
  },
};

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  const now = Date.now();
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks', route => json(route, [readyTask]));
  await page.route('**/api/tasks/grouped', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [readyTask], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths', route => json(route, [{
    name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH,
  }]));
  await page.route('**/api/runner/status', route => json(route, { projects: {} }));
  await page.route('**/api/clients', route => json(route, [{
    id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service',
    registeredAt: new Date(now - 86_400_000).toISOString(),
    lastSeenAt: new Date(now - 5_000).toISOString(), runnerDaemonState: 'running',
  }]));
  await page.route('**/api/v1/management/remote-hosts', route => json(route, [{
    runnerId: 'agent-runner-01', name: 'agent-runner-01', hostId: 'host-a',
    instanceId: 'coding', runnerVersion: '1.3.0', protocolVersion: 3, status: 'active',
    registeredAt: new Date(now - 86_400_000).toISOString(), lastSeenAt: new Date(now - 5_000).toISOString(),
    hostAdmission: { hostId: 'host-a', admissionState: 'open' },
    capabilities: [{
      key: 'cli-execution:claude', category: 'cli-execution', advertisedStatus: 'ready',
      healthState: 'healthy', advertisedAt: new Date(now - 5_000).toISOString(),
      freshUntil: new Date(now + 175_000).toISOString(), isFresh: true,
      consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
    }, {
      key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'unavailable',
      healthState: 'suspect', reason: 'Not logged in', detail: 'Not logged in',
      advertisedAt: new Date(now - 5_000).toISOString(), freshUntil: new Date(now + 175_000).toISOString(),
      isFresh: true, consecutiveFailures: 1, affectedClaims: [], recoveryHistory: [],
    }], telemetry: null,
  }]));
}

test('a Ready card names the provider and host that need sign-in', async ({ page }) => {
  mkdirSync(SHOT_DIR, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await dismissDevErrorDialog(page);
  // Broad API mocks intentionally omit unrelated shell payloads. Keep their
  // dev-only diagnostics from intercepting the provider-auth proof surface.
  await page.addStyleTag({ content: 'app-error-dialog .dialog__overlay { display: none !important; }' });

  const wait = page.getByTestId('task-card-provider-auth-wait');
  await expect(wait).toContainText('Waiting for Claude Code sign-in on agent-runner-01');
  await wait.hover();
  await expect(page.getByRole('tooltip')).toContainText('agent-runner-01: Not logged in');

  const card = page.locator('app-job-card').filter({ hasText: 'Implement with Claude' });
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await card.screenshot({ path: join(SHOT_DIR, `provider-auth-ready-card-${theme}--mocked.png`) });
  }
});
