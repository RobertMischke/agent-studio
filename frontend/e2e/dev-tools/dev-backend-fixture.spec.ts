/**
 * Smoke test for the dev-backend fixture.
 *
 * Proves that the fixture can bring dev's backend up on :5030, surface its
 * health check, and tear it down cleanly. Run this from stable
 * (`PW_TARGET=stable npm run e2e -- e2e/dev-backend-fixture.spec.ts`) so the
 * spec actually exercises "spin dev up from outside" rather than hitting an
 * already-running dev instance.
 */
import { test, expect } from '../fixtures/dev-backend';

test.describe('dev-backend fixture', () => {
  test('starts dev, reports a workspace, and answers /healthz', async ({ devBackend }) => {
    const expectedPort = Number(process.env.DEV_PORT ?? 5030);
    expect(devBackend.port).toBe(expectedPort);
    expect(devBackend.baseUrl).toMatch(new RegExp(`:${expectedPort}$`));
    expect(devBackend.workspace).toBeTruthy();

    const health = await fetch(`${devBackend.baseUrl}/healthz`);
    expect(health.ok).toBe(true);

    const watchPathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
    expect(watchPathsResponse.ok).toBe(true);
    const watchPaths = await watchPathsResponse.json() as { name: string; path: string }[];
    expect(watchPaths.length).toBeGreaterThan(0);

    // Workspace must look like an absolute path; the actual path is environment-
    // dependent so we don't pin it.
    expect(devBackend.workspace.length).toBeGreaterThan(3);
  });
});
