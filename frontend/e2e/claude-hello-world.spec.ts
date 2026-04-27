import { test, expect } from '@playwright/test';
import { getClaudeQuota } from './helpers/quota';
import { createJob, startJob, waitForJob, getJobOutput } from './helpers/jobs';

/**
 * Full-loop smoke test:
 *  1. Verify Claude has quota headroom.
 *  2. Create a tiny job (Hello World prompt) via the REST API.
 *  3. Start it via the API as Claude Code.
 *  4. Poll until the execution finishes.
 *  5. Assert clean status + non-empty output.
 *
 * Marked @billable because it consumes real Claude quota (small, but real).
 * Skipped when SKIP_BILLABLE=1 (e.g. in CI without credentials).
 *
 * NOTE: this spec is API-driven on purpose. The Add-Task UI flow is covered
 * by `add-task.spec.ts`; mixing UI clicks with billable API calls makes
 * failures harder to triage. We verify the *result* is what the UI would
 * show by reading the same backend endpoints the frontend uses.
 */

const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

test.describe('Claude Code — hello world @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  // The CLI run itself can take 1–2 minutes on first cold start.
  test.setTimeout(240_000);

  test('creates, starts and completes a tiny Hello World task', async () => {
    // 1. Quota check.
    const q = await getClaudeQuota();
    expect(q.available, 'Claude must be available').toBe(true);
    expect(q.hasHeadroom, `Claude near quota cap: worst=${q.worstUsedPct}%`).toBe(true);

    // 2. Create. Use a unique title so re-runs don't collide.
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const title = `e2e Hello World ${stamp}`;
    const created = await createJob({
      title,
      watchPath: WATCH_PATH,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      promptMarkdown:
        'Reply with exactly the text "Hello World" and nothing else. Do not edit any files.',
      targetState: '2-ready'
    });
    expect(created.id).toBeTruthy();

    // 3. Start.
    const exec = await startJob(created.id, WATCH_PATH, {
      cliType: 'claude',
      model: 'claude-haiku-4-5'
    });
    expect(exec.processId).toBeGreaterThan(0);
    expect(exec.status).toBe('running');

    // 4. Wait until done. Hello World should finish in well under 2min.
    const finished = await waitForJob(
      created.id,
      WATCH_PATH,
      j => j.execution !== null && j.execution.status !== 'running',
      { timeoutMs: 180_000, intervalMs: 2_000 }
    );

    // 5. Assertions on the terminal state.
    expect(finished.execution).not.toBeNull();
    const e = finished.execution!;
    expect(
      e.status,
      `Expected completed/finished, got ${e.status} (exit=${e.exitCode}, dur=${e.durationSeconds}s)`
    ).toBe('completed');
    expect(e.exitCode, 'Exit code should be 0').toBe(0);

    // Output endpoint should return something non-empty.
    const out = await getJobOutput(created.id, WATCH_PATH);
    expect(out, 'Output endpoint should return data').toBeTruthy();
    const text = JSON.stringify(out);
    expect(text.toLowerCase()).toContain('hello');

    // Regression guard: the Claude CLI used to emit
    //   "Warning: no stdin data received in 3s, proceeding without it."
    // because the backend left stdin open. We now close stdin right after
    // process.Start(), so the warning must not appear in the output stream.
    expect(
      text,
      'stdin warning leaked — backend must close stdin after process.Start()'
    ).not.toMatch(/no stdin data received/i);

    // And the run itself shouldn't pay the 3-second stdin timeout anymore.
    // Haiku Hello World finishes in well under 5 seconds when stdin is closed
    // up front. We allow a generous 15s envelope to absorb cold-start jitter.
    expect(
      e.durationSeconds!,
      `run took ${e.durationSeconds}s — likely the 3s stdin warning regressed`
    ).toBeLessThan(15);
  });
});
