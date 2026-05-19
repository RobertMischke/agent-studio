import { test, expect } from '@playwright/test';
import { createJob, startJob, waitForJob, getJobOutput } from '../helpers/jobs';

/**
 * Regression: when a Claude job is launched, the Activity Log used to stay
 * empty until the model finished — Claude's `-p` mode buffers its entire
 * answer before flushing stdout. From the user's perspective this looked
 * indistinguishable from "stuck": no PID, no started-at line, no progress.
 *
 * The backend now writes a synthetic "[taskboard] Started ... CLI" line into
 * the output buffer the moment the process spawns, and an exit line when it
 * finishes. A 30s heartbeat fills longer silent stretches.
 *
 * @billable — uses real Claude quota (Haiku, fast).
 */

const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

test.describe('Activity log — visibility @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  test.setTimeout(180_000);

  test('synthetic Started + Exited lines appear in the output log', async () => {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const created = await createJob({
      title: `e2e Visibility ${stamp}`,
      watchPath: WATCH_PATH,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      promptMarkdown: 'Reply with the single word OK.',
      targetState: '2-ready'
    });

    await startJob(created.id, WATCH_PATH, {
      cliType: 'claude',
      model: 'claude-haiku-4-5'
    });

    // Within 2s the synthetic Started line must already be in the buffer,
    // even if the model itself hasn't produced anything yet.
    await new Promise(r => setTimeout(r, 2_000));
    const earlyOut = await getJobOutput(created.id, WATCH_PATH);
    const earlyText = JSON.stringify(earlyOut);
    expect(earlyText, 'Started marker missing right after spawn').toMatch(/\[taskboard\] Started claude CLI/);

    // Wait for exit and assert the synthetic exit line appears too.
    await waitForJob(
      created.id,
      WATCH_PATH,
      j => j.execution !== null && j.execution.status !== 'running',
      { timeoutMs: 120_000, intervalMs: 1_500 }
    );

    const finalOut = await getJobOutput(created.id, WATCH_PATH);
    const finalText = JSON.stringify(finalOut);
    expect(finalText).toMatch(/\[taskboard\] claude CLI exited/);
    expect(finalText).toMatch(/\[taskboard\] Started claude CLI/);
  });
});
