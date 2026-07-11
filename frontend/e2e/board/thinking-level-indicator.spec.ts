import { expect, test, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Thinking level indicator';
const WATCH_PATH = 'C:/fixtures/thinking-level-indicator';

function task(id: string, title: string, configured: string, effective?: string) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, title, state: '2-ready', order: id === 'default-level' ? 1 : 2,
    agent: 'codex', cliType: 'codex', createdAt: '2026-07-11T00:00:00Z', watchPath: WATCH_PATH,
    projectName: PROJECT, folderPath: `${WATCH_PATH}/${id}`, lastActivity: '2026-07-11T00:01:00Z',
    sessionName: null, model: 'gpt-5.6-sol', thinkingLevel: configured, useOwnSession: null,
    lastUsage: null, commit: null, ownerClientId: 'local-default', tags: [],
    execution: {
      jobId: id, taskKey: `${WATCH_PATH}::${id}`, processId: 7, startedAt: '2026-07-11T00:00:30Z',
      status: 'completed', exitCode: 0, durationSeconds: 30, model: 'gpt-5.6-sol',
      thinkingLevel: effective ?? configured, runOutcome: 'success',
    },
  };
}

const tasks = [
  task('default-level', 'Default stays quiet', 'high', 'high'),
  task('configured-override', 'Configured override stands out', 'ultra'),
  task('effective-level', 'Effective run metadata wins', 'ultra', 'medium'),
];
const grouped = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: tasks, progress: [], failedPickup: [],
  codeNotComplete: [], review: [], autoReview: [], humanReview: [], escalated: [], completed: [], archive: [],
};

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    if (url.includes('/api/tasks/grouped')) return json(route, grouped);
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, tasks);
    if (url.includes('/api/watch-paths')) return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    if (url.includes('/api/clients')) return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance', defaultCliType: 'codex', defaultModel: 'gpt-5.6-sol', defaultThinkingLevel: 'high' }]);
    if (url.includes('/api/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/quota')) return json(route, { at: '2026-07-11T00:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/cli/usage')) return json(route, { at: '2026-07-11T00:00:00Z', sessions: [] });
    return json(route, []);
  });
}

test('shows the effective level and highlights deviations from the client default in both themes', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');

  const levels = page.getByTestId('task-card-thinking-level');
  await expect(levels).toHaveCount(3);
  await expect(levels.nth(0)).toHaveText('h');
  await expect(levels.nth(0)).toHaveAttribute('data-thinking-level-override', 'false');
  await expect(levels.nth(1)).toHaveText('u');
  await expect(levels.nth(1)).toHaveAttribute('data-thinking-level-override', 'true');
  await expect(levels.nth(2)).toHaveText('m');
  await expect(levels.nth(2)).toHaveAttribute('data-thinking-level', 'medium');
  await expect(levels.nth(2)).toHaveAttribute('data-thinking-level-override', 'true');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    const backgrounds = await levels.evaluateAll(nodes => nodes.map(node => getComputedStyle(node).backgroundColor));
    expect(backgrounds[0]).toBe('rgba(0, 0, 0, 0)');
    expect(backgrounds[1]).not.toBe('rgba(0, 0, 0, 0)');
    expect(backgrounds[2]).not.toBe('rgba(0, 0, 0, 0)');

    await dismissDevErrorDialog(page);
    await expect(page.getByTestId('lane-2-ready')).toBeVisible();
    await page.screenshot({
      path: `C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/tasks/002/AGT-2075/results/thinking-level-card--${theme}.png`,
      fullPage: true,
    });
  }
});
