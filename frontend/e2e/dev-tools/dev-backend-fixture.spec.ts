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
    expect(devBackend.port).toBe(5030);
    expect(devBackend.baseUrl).toMatch(/:5030$/);
    expect(devBackend.workspace).toBeTruthy();

    const health = await fetch(`${devBackend.baseUrl}/healthz`);
    expect(health.ok).toBe(true);

    // Workspace must look like an absolute path; the actual path is environment-
    // dependent so we don't pin it.
    expect(devBackend.workspace.length).toBeGreaterThan(3);
  });
});
