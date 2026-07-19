import { chromium } from '@playwright/test';

const BASE = 'http://127.0.0.1:4012';
const PROJECT = 'fixture-run-activity';
const WATCH_PATH = 'C:/fixtures/run-activity-repo';
const BACKOFF_UNTIL = new Date(Date.now() + 90_000).toISOString();

function makeTask(id, title, order, runActivity) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, key: `ASS-${order + 1750}`, title,
    state: '3-progress', order,
    agent: 'codex', cliType: 'codex', createdAt: '2026-06-10T09:00:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/3-progress/${id}`,
    lastActivity: '2026-06-10T09:30:00Z', sessionName: null,
    model: 'gpt-5-codex', useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default', tags: [],
    runActivity,
  };
}
const PROGRESS = [
  makeTask('ra-active', 'Run-activity active alpha', 1, { kind: 'active', processId: 4242, attempt: 0 }),
  makeTask('ra-backoff', 'Run-activity failed backoff bravo', 2, { kind: 'failed-backoff', backoffUntil: BACKOFF_UNTIL, attempt: 2, lastError: 'git push rejected (non-fast-forward)' }),
  makeTask('ra-failed-idle', 'Run-activity failed idle charlie', 3, { kind: 'failed-idle', attempt: 1, lastError: 'run ended without sentinel' }),
  makeTask('ra-orphan', 'Run-activity orphan delta', 4, { kind: 'no-active-run', attempt: 0 }),
];
const ALL = [...PROGRESS];
const GROUPED_PAYLOAD = { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: PROGRESS, failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [] };

async function installRoutes(page) {
  await page.route('**/api/**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route(/\/api\/tasks\/([^/?]+)(\?|$)/, (route) => {
    const m = route.request().url().match(/\/api\/tasks\/([^/?]+)(?:\?|$)/);
    const id = m ? decodeURIComponent(m[1]) : '';
    const hit = ALL.find((t) => t.id === id);
    const detail = hit ? { info: hit, promptMarkdown: null, promptHistory: [], titleHistory: [], statusMarkdown: null, statusGeneration: null, contextUsage: null, log: [], summaryState: null, reviewEvidence: [] } : {};
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) });
  });
  await page.route('**/api/tasks/grouped**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));
  await page.route('**/api/watch-paths**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]) }));
  await page.route('**/api/environment**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) }));
  await page.route('**/api/cli/usage**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-10T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-10T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/tasks\/[^/?]+\/session-events(\?|$)/, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(\?|$)/, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1100 } });
await page.addInitScript(() => {
  localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' }));
  document.documentElement.dataset['studioTheme'] = 'dark';
});
await installRoutes(page);
await page.goto(BASE + '/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
await page.locator('[data-testid="task-card"]').first().waitFor({ state: 'visible', timeout: 25000 }).catch((e) => console.log('no task-card:', e.message.slice(0,80)));
await page.waitForTimeout(2000);

const dump = await page.evaluate(() => {
  const ng = window.ng;
  const cards = Array.from(document.querySelectorAll('[data-testid="task-card"]'));
  const hostTag = cards[0]?.closest('app-task-card, app-job-card')?.tagName || cards[0]?.parentElement?.tagName || null;
  const probeCard = cards[0];
  let job = null, jobErr = null, badgeState = null, compKeys = null;
  try {
    const comp = ng?.getOwningComponent ? ng.getOwningComponent(probeCard) : (ng?.getComponent ? ng.getComponent(probeCard.parentElement) : null);
    if (comp) compKeys = Object.keys(comp).slice(0, 25);
    const j = comp?.job ? comp.job() : null;
    if (j) {
      job = { state: j.state, hasRunActivity: !!j.runActivity, runActivity: j.runActivity ?? null };
    } else { jobErr = 'no comp/job()'; }
    badgeState = comp && 'runActivityBadge' in comp ? (comp.runActivityBadge() ? JSON.stringify(comp.runActivityBadge()).slice(0,80) : 'null') : 'no-computed';
  } catch (e) { jobErr = String(e).slice(0, 160); }
  return {
    cardCount: cards.length, hostTag,
    hasNg: !!ng,
    anyPill: document.querySelectorAll('[data-testid="task-card-run-activity"]').length,
    probeJob: job, jobErr, badgeState, compKeys,
  };
});
console.log('DUMP:', JSON.stringify(dump, null, 2));
await browser.close();
