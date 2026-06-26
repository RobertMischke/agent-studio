import { test, expect } from '@playwright/test';

/**
 * Add Task dialog - "Parent epic" picker (assignment way 1 in the UI).
 *
 * When creating a `kind=task`, an optional "Parent epic" dropdown lists the
 * epics of the currently selected project (GET /api/epics, filtered client-side
 * by watchPath). Choosing one sends it as CreateTaskRequest.epicId. The picker is
 * hidden for `kind=epic` (an epic has no parent epic) and when the project has
 * no epics.
 *
 * Epics are mocked so the spec is deterministic regardless of the backend's real
 * board; the create POST is intercepted so no real task is written.
 */

const EPIC_ID = 'e2e-parent-epic-pick';
const EPIC_TITLE = 'E2E Parent Epic (pick me)';

interface WatchPath { path: string; name: string }

function epicRollup(id: string, title: string, watchPath: string) {
  return {
    id, title, watchPath,
    projectName: 'e2e', state: '2-ready',
    subTaskTotal: 0, completed: 0, inProgress: 0, open: 0,
    byState: {}, subTasks: [],
  };
}

test.describe('Add Task - parent epic picker', () => {
  test('lists project epics, hides for kind=epic, sends epicId on create', async ({ page }) => {
    // Real watch paths from the (proxied) backend so the mocked epic's
    // watchPath matches a selectable project.
    const wps = await (await page.request.get('/api/watch-paths')).json() as WatchPath[];
    expect(wps.length).toBeGreaterThan(0);
    const target = wps[0];

    // GET /api/epics -> one epic in the target project (way-1 candidate) plus
    // one in a foreign project to prove the watchPath filter excludes it.
    await page.route('**/api/epics**', async (route) => {
      if (route.request().method() !== 'GET') return route.fallback();
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          epicRollup(EPIC_ID, EPIC_TITLE, target.path),
          epicRollup('e2e-foreign-epic', 'Foreign project epic', '/nonexistent/foreign'),
        ]),
      });
    });

    // Intercept the create POST so the assertion can read epicId and no real
    // task is written to the backend.
    let createdBody: Record<string, unknown> | null = null;
    await page.route('**/api/tasks', async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();
      createdBody = route.request().postDataJSON();
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ id: 'e2e-created-task' }),
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    // Pin the project so the mocked epic's watchPath matches the selection.
    await page.getByTestId('create-project-select').selectOption({ value: target.path });

    const picker = page.getByTestId('create-parent-epic-select');
    await expect(picker).toBeVisible();
    // Only the target project's epic is offered (foreign one is filtered out).
    await expect(picker.locator('option')).toHaveCount(2); // "No parent epic" + 1
    await expect(picker.locator('option', { hasText: EPIC_TITLE })).toHaveCount(1);
    await expect(picker.locator('option', { hasText: 'Foreign project epic' })).toHaveCount(0);

    const taskShot = await dialog.screenshot();
    await test.info().attach('parent-epic-picker-task', { body: taskShot, contentType: 'image/png' });

    // Switching Kind to Epic hides the picker (an epic has no parent epic).
    await page.getByTestId('create-kind-epic').click();
    await expect(picker).toBeHidden();
    const epicShot = await dialog.screenshot();
    await test.info().attach('parent-epic-picker-hidden-for-epic', { body: epicShot, contentType: 'image/png' });

    // Back to Task: pick the epic and create.
    await page.getByTestId('create-kind-task').click();
    await expect(picker).toBeVisible();
    await picker.selectOption({ value: EPIC_ID });

    await page.getByTestId('create-title').fill('E2E child task');
    await page.getByTestId('create-submit').click();

    await expect.poll(() => createdBody?.['epicId']).toBe(EPIC_ID);
    expect(createdBody?.['kind']).toBe('task');
  });
});
