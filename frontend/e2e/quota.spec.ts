import { test, expect } from '@playwright/test';
import { getClaudeQuota } from './helpers/quota';
import { api } from './helpers/api';

/**
 * Verifies the Claude quota probe gives a green light. "Quota probe" means
 * the backend's check that we have enough subscription headroom to actually
 * start a Claude task — without it we'd fire a job that fails immediately.
 */

test.describe('Claude quota', () => {
  test('Claude CLI is available with headroom to start a task', async () => {
    const q = await getClaudeQuota();
    expect(q.available, 'Claude CLI must be available').toBe(true);
    expect(q.hasHeadroom, 'Claude must report quota headroom').toBe(true);
  });

  test('quota endpoint responds without error', async () => {
    // /api/cli/quota uses background refresh; we just want a 200.
    const snap = await api('/api/cli/quota');
    expect(snap).toBeTruthy();
  });
});
