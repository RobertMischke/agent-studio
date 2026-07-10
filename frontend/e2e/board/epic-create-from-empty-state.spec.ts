/**
 * ASS-597: per-project Epics empty-state + create dialog. Opening a project's
 * "Epics" explorer link scopes the overview to that project (a chip echoes the
 * name) and lights up the create affordances the cross-project rollup hides:
 * a header "+ New epic" button and, when the project has no epics yet, a
 * centered "Create your first epic" invitation. Submitting the dialog posts a
 * `kind=epic` card into `0-backlog` (so it never trips the Ready pickup gate
 * into a decomposition run) and the new epic then shows up in the same scoped
 * list.
 *
 * The test seeds a single non-epic placeholder so the target project renders a
 * row in the explorer while the scoped epic list stays empty, drives the UI
 * create flow, and asserts the freshly created epic appears. Routes are
 * `/api/tasks*`; the task helpers are inlined so this does not depend on the
 * still-`/api/tasks` shared helpers.
 */
import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; title: string; state: string; watchPath: string; kind?: string; }

const PREFIX = 'e2e-epic-empty-';

async function getTestWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => p.name === 'Playwright Test') ?? paths[0];
}

async function listTasks(): Promise<TaskRow[]> {
  return api<TaskRow[]>('/api/tasks?includeFixtures=true');
}

async function createTask(input: { id: string; title: string; watchPath: string; kind?: string; targetState?: string }): Promise<string> {
  const res = await api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id,
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: null,
      targetState: input.targetState ?? '0-backlog',
      kind: input.kind ?? 'task',
      fixture: false,
    }),
  });
  return res.id;
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

/** Open the scoped Epics overview for a named project via the explorer tree. */
async function openProjectEpics(page: Page, name: string): Promise<void> {
  const row = page.getByTestId(`studio-explorer-project-row-${name}`);
  await expect(row).toBeVisible({ timeout: 15_000 });
  const epicsLink = page.getByTestId(`studio-explorer-project-epics-${name}`);
  if (!(await epicsLink.isVisible().catch(() => false))) {
    await row.locator('button.tree-row').first().click();
  }
  await epicsLink.click();
}

test.describe('Epics empty-state + create dialog (per project)', () => {
  test.beforeEach(() => test.setTimeout(120_000));
  test.afterEach(() => cleanup());

  test('scoped Epics invites creation, and the new epic appears in the list', async ({ page }, testInfo) => {
    const wp = await getTestWatchPath();
    await cleanup();
    // A non-epic placeholder makes the project visible in the explorer while
    // the scoped epic list stays empty, so the invite empty-state renders.
    await createTask({ id: `${PREFIX}placeholder`, title: `${PREFIX}placeholder`, watchPath: wp.path });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.evaluate(() => {
      try { localStorage.removeItem('atp.studio.explorerSections'); } catch { /* ignore */ }
      try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
    });
    await page.reload();
    await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 10_000 });

    await openProjectEpics(page, wp.name);

    const screen = page.getByTestId('epic-overview-screen');
    await expect(screen).toBeVisible({ timeout: 10_000 });
    await expect(page).toHaveURL(/#\/epics/, { timeout: 5_000 });
    // Scoped: the header chip echoes the project, and create affordances show.
    await expect(page.getByTestId('epic-overview-scope')).toHaveText(wp.name);
    await expect(page.getByTestId('epic-overview-new')).toBeVisible();
    // Empty (no epics in this project yet): the invitation CTA renders.
    await expect(page.getByTestId('epic-overview-empty')).toBeVisible();
    await expect(page.getByTestId('epic-overview-create')).toBeVisible();

    const emptyShot = testInfo.outputPath('epics-empty-invite.png');
    await page.screenshot({ path: emptyShot, fullPage: false });
    await testInfo.attach('epics-empty-invite', { path: emptyShot, contentType: 'image/png' });

    // Open the dialog and provide both required intent fields. Requiring a
    // concrete goal prevents unexplained empty placeholder epics.
    await page.getByTestId('epic-overview-create').click();
    const dialog = page.getByTestId('epic-create-dialog');
    await expect(dialog).toBeVisible({ timeout: 5_000 });
    const title = `${PREFIX}Checkout revamp`;
    await page.getByTestId('epic-create-title').fill(title);
    await page.getByTestId('epic-create-description').fill('Split checkout work into reviewable delivery tasks.');

    const dialogShot = testInfo.outputPath('epic-create-dialog.png');
    await page.screenshot({ path: dialogShot, fullPage: false });
    await testInfo.attach('epic-create-dialog', { path: dialogShot, contentType: 'image/png' });

    await page.getByTestId('epic-create-submit').click();

    // Dialog closes; the new epic shows up in the scoped list.
    await expect(dialog).toHaveCount(0, { timeout: 10_000 });
    const card = page.getByTestId('epic-overview-card').filter({ hasText: title });
    await expect(card).toBeVisible({ timeout: 15_000 });

    const createdShot = testInfo.outputPath('epic-created-in-list.png');
    await page.screenshot({ path: createdShot, fullPage: false });
    await testInfo.attach('epic-created-in-list', { path: createdShot, contentType: 'image/png' });
  });
});
