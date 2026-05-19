import { test, expect } from '@playwright/test';
import { createJob, startJob, waitForJob } from '../helpers/jobs';
import { api } from '../helpers/api';

/**
 * Verifies that the live `rate_limit_event` snapshot is captured from the
 * Claude CLI's stream-json output and surfaced via /claude/session-info.
 *
 * @billable — uses real Claude quota (Haiku, fast).
 */

const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

interface SessionResp {
  sessionInfo: { sessionId: string };
  rateLimit: {
    window: string | null;
    status: string | null;
    resetsAt: number;
    overageStatus: string | null;
    isUsingOverage: boolean;
    capturedAt: string;
  } | null;
}

test.describe('Claude — live rate-limit capture @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  test.setTimeout(180_000);

  test('rate_limit_event is captured into the live session snapshot', async () => {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const created = await createJob({
      title: `e2e RateLimit ${stamp}`,
      watchPath: WATCH_PATH,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      promptMarkdown: 'Say hi.',
      targetState: '2-ready'
    });

    await startJob(created.id, WATCH_PATH, { cliType: 'claude', model: 'claude-haiku-4-5' });
    await waitForJob(
      created.id, WATCH_PATH,
      j => j.execution !== null && j.execution.status !== 'running',
      { timeoutMs: 120_000, intervalMs: 1_000 }
    );

    // The CLI emits at least one rate_limit_event per turn; ProcInfo retains
    // the last snapshot until the next run on the same jobKey replaces it.
    const url = `/api/jobs/${encodeURIComponent(created.id)}/claude/session-info?watchPath=${encodeURIComponent(WATCH_PATH)}`;
    const res = await api<SessionResp>(url);

    expect(res.rateLimit, 'rateLimit should be populated after a Claude run').not.toBeNull();
    const rl = res.rateLimit!;
    expect(rl.window).toBeTruthy();
    expect(['allowed', 'exceeded', 'warning']).toContain(rl.status);
    expect(rl.resetsAt).toBeGreaterThan(0);
    // resetsAt is a Unix epoch in seconds, well into the future on a healthy
    // account. Sanity-cap it well under year-3000 just to catch a bug where
    // the value is read in milliseconds by mistake.
    expect(rl.resetsAt).toBeLessThan(32_000_000_000);
    expect(typeof rl.isUsingOverage).toBe('boolean');
    expect(rl.capturedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);

    // Cleanup: the test job is now in 4-review.
    await api(`/api/jobs/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(WATCH_PATH)}`, { method: 'DELETE' });
  });
});
