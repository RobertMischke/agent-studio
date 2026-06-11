import { test, expect } from '@playwright/test';
import { getGeminiAvailability } from '../helpers/quota';
import { createJob, startJob, waitForJob, getJobOutput } from '../helpers/jobs';

/**
 * Full-loop smoke test for Gemini, mirroring the Claude billable hello-world.
 *
 *  1. Verify the gemini CLI is installed (via `/api/cli/usage`).
 *  2. Create a tiny "Hello World" job via REST.
 *  3. Start it as `cliType=gemini` with a small/cheap model.
 *  4. Poll until the execution finishes.
 *  5. Assert clean status + output mentions "hello".
 *
 * Marked @billable because it consumes real Gemini quota.
 * Skipped when SKIP_BILLABLE=1.
 */

const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

test.describe('Gemini — hello world @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  test.setTimeout(240_000);

  test('creates, starts and completes a tiny Hello World task', async () => {
    // 1. Availability — we don't gate on quota numbers because the Gemini
    //    probe doesn't surface them (see docs/cli/supported-clis.md §3.4).
    const a = await getGeminiAvailability();
    expect(a.available, 'gemini CLI must be available (npm i -g @google/gemini-cli)').toBe(true);

    // 2. Create.
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const title = `e2e Gemini Hello World ${stamp}`;
    const created = await createJob({
      title,
      watchPath: WATCH_PATH,
      agent: 'gemini',
      cliType: 'gemini',
      // Cheapest deterministic option that still proves the path. Auto-routing
      // would also work but we want a known-cheap model in tests.
      model: 'gemini-2.5-flash-lite',
      promptMarkdown:
        'Reply with exactly the text "Hello World" and nothing else. Do not edit any files.',
      targetState: '2-ready'
    });
    expect(created.id).toBeTruthy();

    // 3. Start.
    const exec = await startJob(created.id, WATCH_PATH, {
      cliType: 'gemini',
      model: 'gemini-2.5-flash-lite'
    });
    expect(exec.processId).toBeGreaterThan(0);
    expect(exec.status).toBe('running');

    // 4. Wait for finish. Cold start of the bundled CLI on Windows can be slow.
    const finished = await waitForJob(
      created.id,
      WATCH_PATH,
      j => j.execution !== null && j.execution.status !== 'running',
      { timeoutMs: 180_000, intervalMs: 2_000 }
    );

    // 5. Terminal-state assertions.
    expect(finished.execution).not.toBeNull();
    const e = finished.execution!;
    expect(
      e.status,
      `Expected completed, got ${e.status} (exit=${e.exitCode}, dur=${e.durationSeconds}s)`
    ).toBe('completed');
    expect(e.exitCode, 'Exit code should be 0').toBe(0);

    // The session id captured from the init frame should be a UUID.
    const updated = finished.info.sessionName;
    expect(updated, 'sessionName must be populated from the init frame').toBeTruthy();
    expect(updated).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);

    // Output should contain "hello".
    const out = await getJobOutput(created.id, WATCH_PATH);
    const text = JSON.stringify(out).toLowerCase();
    expect(text).toContain('hello');

    // Regression guards specific to the Gemini integration:
    //  - The "YOLO mode is enabled." stderr lines must surface (they're how we
    //    know the bypass actually engaged).
    //  - The "Result success ... tokens, ...ms" marker must appear (proves
    //    TransformReadLine handled the result frame).
    expect(text).toMatch(/yolo mode is enabled/);
    expect(text).toMatch(/result success/);
  });
});
