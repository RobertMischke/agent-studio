import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name: string; rootPath: string }
interface SessionEvent {
  ts: string;
  kind: 'start' | 'continue' | 'recovery';
  cli: string | null;
  inputSessionId: string | null;
  capturedSessionId: string | null;
  resumed: boolean;
  reason: string | null;
}
interface SessionEventsResponse {
  events: SessionEvent[];
  sessionChain: string[];
  currentSessionId: string | null;
}

/**
 * Locks in the user-visible contract of the session-recovery feature.
 *
 * Two surfaces matter, and both regressed before this PR existed:
 *   1. Continue must not fail with 400 when a job has no recorded session —
 *      the user explicitly does not want their work blocked by a missing
 *      session id. The recovery branch starts a fresh CLI run that
 *      reconstructs context from the job folder.
 *   2. Every start / continue / recovery is recorded in the per-job
 *      session-events log so the protocol-pane chip can show "session
 *      continued" vs "session lost" without the user guessing.
 *
 * The spec only exercises the API surface. We don't burn quota by actually
 * starting a CLI — we POST `/continue` against a brand-new job (no session)
 * and verify both the recovery semantics and the telemetry.
 */
test.describe('Detail — session events + recovery continue', () => {
  test('GET /session-events on a brand-new job returns an empty contract', async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const wp = paths.find(p => p.name.toLowerCase().includes('agent task processor'))!;
    const job = await createJob({
      title: `sess-${Date.now()}`,
      watchPath: wp.path,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });

    try {
      const url = `/api/jobs/${encodeURIComponent(job.id)}/session-events?watchPath=${encodeURIComponent(wp.path)}`;
      const res = await api<SessionEventsResponse>(url);

      expect(res).toBeDefined();
      expect(Array.isArray(res.events)).toBe(true);
      expect(Array.isArray(res.sessionChain)).toBe(true);
      expect(res.events).toHaveLength(0);
      expect(res.sessionChain).toHaveLength(0);
      expect(res.currentSessionId).toBeNull();
    } finally {
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    }
  });

  test('Continue on a job with no session does not 400 — falls back to recovery', async () => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const wp = paths.find(p => p.name.toLowerCase().includes('agent task processor'))!;
    const job = await createJob({
      title: `recov-${Date.now()}`,
      watchPath: wp.path,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });

    try {
      // POST /continue with no session ever recorded. Before this PR, the
      // backend returned 400 with "This job has no session yet — start it
      // once before continuing." After the recovery refactor it must accept
      // the call. The CLI itself may immediately fail (no API key, no quota)
      // — we only assert that the *taskboard* API stopped rejecting it.
      const url = `/api/jobs/${encodeURIComponent(job.id)}/continue?watchPath=${encodeURIComponent(wp.path)}`;
      const res = await fetch(`http://127.0.0.1:5030${url}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ prompt: 'continue please' })
      });

      // The error message we used to return is gone — that's the regression
      // guard. A 400 with any body would still fail this assertion if the
      // body matches the old text.
      const text = await res.text();
      expect(text).not.toContain('This job has no session yet');

      // Stop whatever may have started so the test doesn't leave a CLI
      // running. Tolerant — if the CLI never started, /stop is a no-op.
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}/stop?watchPath=${encodeURIComponent(wp.path)}`,
        { method: 'POST' }
      ).catch(() => {});

      // The session-events log should now record the recovery attempt
      // (whether the CLI itself succeeded is irrelevant — the event row is
      // written before the CLI process spawns).
      const eventsUrl = `/api/jobs/${encodeURIComponent(job.id)}/session-events?watchPath=${encodeURIComponent(wp.path)}`;
      // Give the backend a beat to flush the JSONL line.
      await new Promise(r => setTimeout(r, 500));
      const events = await api<SessionEventsResponse>(eventsUrl);
      const hasRecovery = events.events.some(e => e.kind === 'recovery' && e.resumed === false);
      expect(hasRecovery).toBe(true);
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}/stop?watchPath=${encodeURIComponent(wp.path)}`,
        { method: 'POST' }
      ).catch(() => {});
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    }
  });

  test('protocol pane shows the session chip after a recovery event is recorded', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const wp = paths.find(p => p.name.toLowerCase().includes('agent task processor'))!;
    const job = await createJob({
      title: `chip-${Date.now()}`,
      watchPath: wp.path,
      cliType: 'claude',
      agent: 'claude',
      targetState: '2-ready'
    });

    try {
      // Trigger a recovery event without actually running the CLI to
      // completion: POST /continue on a session-less job, then immediately
      // stop. The event row is written synchronously before the CLI spawns.
      await fetch(`http://127.0.0.1:5030/api/jobs/${encodeURIComponent(job.id)}/continue?watchPath=${encodeURIComponent(wp.path)}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ prompt: 'noop' })
      });
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}/stop?watchPath=${encodeURIComponent(wp.path)}`,
        { method: 'POST' }
      ).catch(() => {});

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      // Open the job detail. The poller hits /session-events on mount.
      await page.locator(`[data-testid="job-card"]`, { hasText: job.id }).first().click({ trial: true }).catch(() => {});

      // Chip is rendered by the protocol pane. data-testid is set to
      // `session-chip-<kind>` — for a recovery row, kind is 'lost'. The
      // poller has a 10 s cadence; bound the wait at 15 s.
      const chip = page.getByTestId('session-chip-lost');
      await expect(chip).toBeVisible({ timeout: 15_000 });
      await expect(chip).toContainText(/session lost/i);
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}/stop?watchPath=${encodeURIComponent(wp.path)}`,
        { method: 'POST' }
      ).catch(() => {});
      await api(`/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(wp.path)}`, { method: 'DELETE' });
    }
  });
});
