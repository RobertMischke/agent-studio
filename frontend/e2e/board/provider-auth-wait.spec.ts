import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/provider-auth-wait';
const PROJECT = 'Provider auth wait';

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page, provider: 'claude' | 'codex' = 'claude'): Promise<void> {
  const now = new Date();
  let codexCompleted = false;
  let codexPolls = 0;
  const task = {
    id: 'AGT-AUTH-WAIT', key: 'AGT-AUTH-WAIT', displayKey: 'AGT-AUTH-WAIT',
    taskKey: `${WATCH_PATH}::AGT-AUTH-WAIT`, title: `${provider} task waiting for host authentication`,
    state: '2-ready', order: 1, agent: provider, cliType: provider,
    createdAt: now.toISOString(), watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/AGT-AUTH-WAIT`, lastActivity: now.toISOString(),
    sessionName: null, model: provider === 'claude' ? 'claude-sonnet-5' : 'gpt-5.4', useOwnSession: null,
    lastUsage: null, execution: null, commit: null, ownerClientId: null, tags: [],
    executionLocation: {
      state: 'queued-remote', executionKind: 'remote', runnerId: 'agent-runner-01',
      configuredRunnerId: 'agent-runner-01', hostDisplayName: 'runner-berlin',
      connectionState: 'connected', leaseState: 'queued',
      trustReason: 'Project execution assignment targets agent-runner-01.',
    },
  };
  const grouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [task], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  };
  const capability = (key: string, status: string, detail?: string) => ({
    key, category: key.split(':')[0], advertisedStatus: status, healthState: 'healthy',
    reason: null, advertisedAt: new Date(now.getTime() + (codexCompleted ? 5_000 : 0)).toISOString(),
    freshUntil: new Date(now.getTime() + 120_000).toISOString(), isFresh: true,
    firstFailureAt: null, lastFailureAt: null, cooldownUntil: null, canaryClaimId: null,
    consecutiveFailures: 0, version: null, identity: key.split(':')[1], detail,
    affectedClaims: [], recoveryHistory: [],
  });

  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/auth/status')) return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    if (url.includes('/api/tasks/grouped')) return json(route, grouped);
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, [task]);
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/codex-sign-in/') && route.request().method() === 'GET') {
      codexCompleted = ++codexPolls > 1;
      return json(route, {
        handle: 'ready-card-handle', hostId: 'agent-runner-01', provider: 'codex', state: codexCompleted ? 'completed' : 'pending',
        detail: codexCompleted ? 'Codex sign-in completed.' : 'Waiting for browser authentication.', requestedAt: now.toISOString(),
        expiresAt: new Date(now.getTime() + 900_000).toISOString(), completedAt: codexCompleted ? new Date().toISOString() : null,
      });
    }
    if (url.endsWith('/codex-sign-in') && route.request().method() === 'POST') return json(route, {
      handle: 'ready-card-handle', hostId: 'agent-runner-01', provider: 'codex', state: 'pending',
      verificationUrl: 'https://auth.openai.com/codex/device', userCode: 'CARD-CODE',
      expiresAt: new Date(now.getTime() + 900_000).toISOString(),
    });
    if (url.includes('/api/v1/management/remote-hosts')) return json(route, [{
      runnerId: 'agent-runner-01', name: 'runner-berlin', hostId: 'host-berlin',
      instanceId: 'coding', runnerVersion: '1.2.0', protocolVersion: 2, status: 'active',
      registeredAt: now.toISOString(), lastSeenAt: now.toISOString(),
      hostAdmission: { hostId: 'host-berlin', admissionState: 'open' },
      capabilities: [
        capability(`cli-execution:${provider}`, 'ready'),
        capability(`provider-auth:${provider}`, codexCompleted ? 'ready' : 'unavailable', codexCompleted ? 'Active session confirmed' : 'Not logged in'),
      ], telemetry: null,
    }]);
    if (url.includes('/telemetry?window=')) return json(route, {
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    if (url.includes('/api/clients')) return json(route, [{
      id: 'agent-runner-01', displayName: 'runner-berlin', kind: 'service',
      registeredAt: now.toISOString(), lastSeenAt: now.toISOString(),
    }]);
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/quota')) return json(route, { at: now.toISOString(), ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/cli/usage')) return json(route, { at: now.toISOString(), sessions: [] });
    return json(route, []);
  });
}

test('Ready card shows the provider sign-in wait reason in both themes', async ({ page }) => {
  const resultsDir = resolve(process.env.JOB_RESULTS_DIR ?? '../results', 'provider-auth');
  mkdirSync(resultsDir, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });

  const wait = page.getByTestId('task-card-provider-auth-wait');
  await expect(wait).toContainText('Waiting for Claude sign-in on runner-berlin');
  await wait.hover();
  await expect(page.getByRole('tooltip')).toContainText('Not logged in');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await page.getByTestId('lane-2-ready').screenshot({
      path: join(resultsDir, `ready-card-provider-auth-wait-${theme}--mocked.png`),
    });
  }
});

test('Ready-card Codex wait action completes device sign-in without a terminal', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page, 'codex');
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });

  const wait = page.getByTestId('task-card-provider-auth-wait');
  await expect(wait).toContainText('Sign in Codex');
  await expect(page.locator('app-codex-sign-in-dialog')).toHaveCount(1);
  await wait.click();
  await expect(page.getByTestId('codex-sign-in-dialog')).toBeVisible();
  await page.getByTestId('codex-sign-in-start').click();
  await expect(page.getByTestId('codex-sign-in-code')).toContainText('CARD-CODE');
  await expect(page.getByTestId('codex-sign-in-dialog')).toBeHidden({ timeout: 10_000 });
  await expect(wait).toBeHidden();
});
