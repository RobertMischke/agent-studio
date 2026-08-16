import { test, expect } from '@playwright/test';

/**
 * W34 S4 public read-only edge, browser side.
 *
 * The server owns the boundary: it refuses every unsafe method with a typed
 * denial. The UI's job is to explain that boundary, so a visitor never sees a
 * live-looking control whose request would be refused. The spec therefore
 * checks both halves against the same running instance: the explanation, and
 * the typed denial a raw client gets.
 *
 * The spec runs only against an instance that reports the public-demo profile
 * (`bash scripts/worktree-test-stack.sh up --demo` with
 * `Security__Profile=public-demo`). Everywhere else it skips, because a
 * read-only banner must never appear in an operator installation.
 */
test.describe('public demo read-only surface', () => {
  test.beforeEach(async ({ request }) => {
    const res = await request.get('/api/environment');
    expect(res.ok(), '/api/environment must respond').toBeTruthy();
    const body = await res.json();
    test.skip(body?.publicDemo?.readOnly !== true, 'not a public-demo instance');
  });

  test('explains read-only mode and disables the create affordances', async ({ page }) => {
    await page.goto('/');

    const banner = page.getByTestId('public-demo-banner');
    await expect(banner).toBeVisible();
    await expect(banner).toContainText('read-only');

    for (const control of [
      page.getByTestId('studio-board-add-task'),
      page.getByRole('button', { name: /Add Task/i }).first(),
    ]) {
      if (await control.count()) await expect(control).toBeDisabled();
    }

    await page.screenshot({ path: 'test-results/public-demo-read-only--real.png', fullPage: false });
  });

  test('a raw unsafe request returns the typed denial', async ({ request }) => {
    const denied = await request.post('/api/tasks', { data: {}, failOnStatusCode: false });

    expect(denied.status()).toBe(403);
    const body = await denied.json();
    expect(body.error).toBe('public-demo-read-only');
  });

  test('an unlisted read is denied by default', async ({ request }) => {
    const denied = await request.get('/api/v1/management/status', { failOnStatusCode: false });

    expect(denied.status()).toBe(403);
    expect((await denied.json()).error).toBe('public-demo-route-denied');
  });

  test('the edge contract is publicly verifiable', async ({ request }) => {
    const edge = await request.get('/api/public-demo/edge');

    expect(edge.ok()).toBeTruthy();
    const body = await edge.json();
    expect(body.readOnly).toBe(true);
    expect(body.profile).toBe('public-demo-readonly');
    expect(body.allowlistDigest).toMatch(/^sha256:[0-9a-f]{64}$/);
  });
});
