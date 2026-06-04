/**
 * Acceptance for the editable Epic detail view (ASS-662 follow-up: "Epic view
 * editable — title + properties, Edit & Status screen"). When the open card in
 * the task detail is an epic (kind=epic), the epic pane is no longer a pure
 * status display: its own properties — description (prompt.md), cross-references
 * and model/CLI — are editable inline and persist through the same API the
 * regular task-edit surfaces use, while the sub-task-by-lane status board below
 * stays put. The title remains editable through the detail header.
 *
 * The test seeds one epic + two sub-tasks via the API, deep-links to the epic's
 * detail, and proves three things: (1) the editable details section AND the
 * status board both render (Edit & Status), (2) editing the description through
 * the inline editor persists to the backend and the rendered view follows, and
 * (3) renaming the epic via the Overview hero title persists. (The studio
 * shell hides the legacy kanban detail header, so the hero title in the
 * Overview tab is the canonical rename affordance.)
 *
 * Routes are `/api/tasks*`; this spec inlines the task API calls it needs so it
 * does not depend on the still-`/api/jobs` shared helpers/jobs.ts.
 */
import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; kind?: string; epicId?: string | null; }
interface TaskDetail { info: { id: string; title: string; kind?: string }; promptMarkdown?: string | null; }

const PREFIX = 'e2e-epic-editable-';

async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskRow[]> {
  return api<TaskRow[]>('/api/tasks?includeFixtures=true');
}

async function createTask(input: {
  id: string;
  title: string;
  watchPath: string;
  kind?: string;
  epicId?: string;
  targetState?: string;
  promptMarkdown?: string | null;
}): Promise<string> {
  const res = await api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id,
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: input.promptMarkdown ?? null,
      targetState: input.targetState ?? '2-ready',
      kind: input.kind ?? 'task',
      epicId: input.epicId ?? null,
      fixture: false,
    }),
  });
  return res.id;
}

async function getDetail(jobId: string, watchPath: string): Promise<TaskDetail> {
  return api<TaskDetail>(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`);
}

async function deleteTask(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID || 'local-default' },
  });
}

async function cleanup(): Promise<void> {
  const all = await listTasks();
  const stale = all.filter(j => j.id.startsWith(PREFIX));
  await Promise.all(stale.map(j => deleteTask(j.id, j.watchPath).catch(() => {})));
}

test.describe('Epic detail: editable title + properties (Edit & Status)', () => {
  test.describe.configure({ timeout: 180_000, retries: 1 });
  test.afterEach(() => cleanup());

  test('description + title edits persist; status board stays', async ({ page }, testInfo) => {
    const wp = await getTestWatchPath();
    const watchPath = wp.path;
    await cleanup();

    const epicId = await createTask({
      id: `${PREFIX}epic`,
      title: `${PREFIX}Checkout revamp`,
      watchPath,
      kind: 'epic',
      targetState: '2-ready',
      promptMarkdown: '# Epic brief\n\nInitial planning notes.',
    });

    for (const n of ['1', '2']) {
      await createTask({
        id: `${PREFIX}sub-${n}`,
        title: `${PREFIX}sub ${n}`,
        watchPath,
        epicId,
        targetState: '2-ready',
      });
    }

    await page.goto(`/?job=${encodeURIComponent(epicId)}&watchPath=${encodeURIComponent(watchPath)}`);
    await expect(page.locator('[data-testid="studio-task"]')).toBeVisible({ timeout: 20_000 });

    // Edit & Status both present: the editable details section and the
    // sub-task-by-lane board live in the same epic pane.
    const pane = page.locator('[data-testid="epic-rollup-pane"]');
    await expect(pane).toBeVisible({ timeout: 15_000 });
    await expect(pane.locator('[data-testid="epic-details"]')).toBeVisible({ timeout: 15_000 });
    await expect(pane.locator('[data-testid="epic-rollup-board"]')).toBeVisible({ timeout: 15_000 });
    await expect(pane.locator('[data-testid="epic-rollup-count"]')).toHaveText('0 / 2 done', { timeout: 15_000 });

    // The seeded description renders in the read view.
    const desc = pane.locator('[data-testid="epic-description"]');
    await expect(desc).toContainText('Initial planning notes', { timeout: 15_000 });

    // --- Edit the description inline -------------------------------------
    await desc.locator('[data-testid="epic-description-edit"]').click();
    const editor = page.locator('[data-testid="prompt-editor"]');
    await expect(editor).toBeVisible({ timeout: 10_000 });

    // Markdown source mode is the reliable way to set exact text.
    await editor.getByTestId('prompt-editor-mode-toggle').click();
    await page.getByTestId('prompt-editor-mode-menu-item-source').click();
    const source = page.getByTestId('prompt-editor-source');
    await expect(source).toBeVisible();

    const marker = `Edited by e2e ${Date.now()}`;
    await source.fill(`# Epic brief\n\n${marker}`);

    // Autosave (600ms debounce) routes through the host's prompt.md PUT.
    await expect.poll(async () => (await getDetail(epicId, watchPath)).promptMarkdown ?? '', { timeout: 10_000 })
      .toContain(marker);
    // The editor closes and the rendered view reflects the saved text.
    await expect(desc).toContainText(marker, { timeout: 10_000 });

    const editShot = testInfo.outputPath('epic-detail-editable.png');
    await page.screenshot({ path: editShot, fullPage: false });
    await testInfo.attach('epic-detail-editable', { path: editShot, contentType: 'image/png' });

    // --- Rename the epic via the Overview hero title ---------------------
    // The studio shell hides the kanban detail header, so the canonical
    // rename affordance is the Overview tab's hero title (it routes through
    // the same setJobTitle PUT the rest of task-edit uses). Overview is the
    // default-active tab, so the hero title is already on screen.
    const newTitle = `${PREFIX}Renamed ${Date.now()}`;
    const heroTitle = page.getByTestId('overview-title');
    await expect(heroTitle).toBeVisible({ timeout: 10_000 });
    await heroTitle.click();
    const titleInput = page.getByTestId('overview-title-input');
    await expect(titleInput).toBeVisible({ timeout: 10_000 });
    await titleInput.fill(newTitle);
    await titleInput.press('Enter');

    await expect.poll(async () => (await getDetail(epicId, watchPath)).info.title, { timeout: 10_000 })
      .toBe(newTitle);
    await expect(page.getByTestId('overview-title')).toContainText(newTitle, { timeout: 10_000 });

    // Status board is untouched by the edits.
    await expect(pane.locator('[data-testid="epic-rollup-board"]')).toBeVisible();
    await expect(pane.locator('[data-testid="epic-rollup-count"]')).toHaveText('0 / 2 done');
  });
});
