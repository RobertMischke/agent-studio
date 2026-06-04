import { expect, Page, test } from '@playwright/test';
import * as path from 'path';

/**
 * Task-detail epic-membership banner + quick back-jump.
 *
 * When the open card is a sub-task of an epic (epicId set, kind != epic) the
 * detail view shows a clickable banner under the header with the epic's
 * key + title; clicking it opens the epic's own detail. Epics themselves and
 * epic-less tasks show no banner.
 *
 * The spec drives the live frontend (proxied to a real backend): it picks a
 * real sub-task off the board, then pins the one piece the banner keys off —
 * the epic rollup (GET /api/epics/{id}) — so the key chip is deterministic
 * even though the project's real epics predate stable keys. Navigation and the
 * detail payloads come from the backend the frontend proxies to.
 *
 * Screenshots land under JOB_RESULTS_DIR/epic-membership when the orchestrator
 * sets it (or EPIC_MEMBERSHIP_SHOTS), else test-results/ (scratch).
 */

const SHOTS_DIR = process.env.EPIC_MEMBERSHIP_SHOTS?.trim()
  || (process.env.JOB_RESULTS_DIR
    ? path.join(process.env.JOB_RESULTS_DIR, 'epic-membership')
    : path.resolve(__dirname, '../../test-results/epic-membership'));

const MOCK_KEY = 'ASS-597';
const MOCK_TITLE = 'EPIC: Epics-Feature Ausbau';

// Deep-linking to a backlog/preparation card does not reliably mount the
// detail view; pick a card in a "running" lane exactly like the sibling
// task-detail specs so the header is guaranteed to render.
const MOUNTABLE = new Set(['3-progress', '4-auto-review', '5-human-review', '6-completed']);

interface TaskLite { id: string; watchPath: string; epicId?: string | null; kind?: string; state?: string; }

async function fetchTasks(page: Page): Promise<TaskLite[]> {
  const res = await page.request.get('/api/tasks');
  if (!res.ok()) return [];
  const tasks = await res.json();
  return Array.isArray(tasks) ? (tasks as TaskLite[]) : [];
}

/** First task matching `pred`, preferring a mountable lane, else any match. */
function pick(tasks: TaskLite[], pred: (t: TaskLite) => boolean): TaskLite | null {
  return tasks.find((t) => pred(t) && MOUNTABLE.has(t.state ?? '')) ?? tasks.find(pred) ?? null;
}

async function mockEpic(page: Page, epicId: string, watchPath: string): Promise<void> {
  const body = JSON.stringify({
    id: epicId, key: MOCK_KEY, title: MOCK_TITLE, projectName: 'agent-taskboard',
    watchPath, state: '2-ready', subTaskTotal: 0, completed: 0, inProgress: 0, open: 0,
    byState: {}, subTasks: [],
  });
  await page.route(`**/api/epics/${encodeURIComponent(epicId)}**`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body }));
}

async function openTask(page: Page, t: TaskLite): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/?job=${encodeURIComponent(t.id)}&watchPath=${encodeURIComponent(t.watchPath)}`);
}

test.describe('Task-detail epic-membership banner', () => {
  test('sub-task shows its epic (key + title) and jumps back to the epic', async ({ page }) => {
    const sub = pick(await fetchTasks(page), (t) => !!t.epicId && t.kind !== 'epic');
    if (!sub) { test.skip(true, 'No sub-task with an epicId on the board.'); return; }
    const epicId = sub.epicId!;
    await mockEpic(page, epicId, sub.watchPath);
    await openTask(page, sub);

    const banner = page.getByTestId('epic-membership-banner');
    await expect(banner).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('epic-membership-key')).toHaveText(MOCK_KEY);
    await expect(page.getByTestId('epic-membership-title')).toContainText('Epics-Feature Ausbau');

    await page.screenshot({ path: path.join(SHOTS_DIR, 'sub-task-epic-banner.png'), fullPage: false });

    // Back-jump: clicking opens the epic's own detail. The epic is kind=epic,
    // so the membership banner disappears and the URL points at the epic.
    await banner.click();
    await expect.poll(() => new URL(page.url()).searchParams.get('job'), { timeout: 15_000 })
      .toBe(epicId);
    await expect(page.getByTestId('epic-membership-banner')).toHaveCount(0);

    await page.screenshot({ path: path.join(SHOTS_DIR, 'after-jump-to-epic.png'), fullPage: false });
  });

  test('a task without an epic shows no banner', async ({ page }) => {
    const plain = pick(await fetchTasks(page), (t) => !t.epicId && t.kind !== 'epic');
    if (!plain) { test.skip(true, 'No epic-less task on the board.'); return; }
    await openTask(page, plain);
    // The detail view must mount before we can assert the banner is absent.
    // detail-panes is the always-present pane container (the project chip in
    // the header is conditionally hidden, so it is not a reliable readiness cue).
    await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('epic-membership-banner')).toHaveCount(0);
  });
});
