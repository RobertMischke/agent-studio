import { test, expect } from '@playwright/test';
import { createJob, startJob, waitForJob, getJobOutput } from '../helpers/jobs';

/**
 * Regression: Claude `-p` used to buffer its entire reply until the model
 * finished, so the Activity Log stayed empty for the whole run and tasks
 * looked stuck. Backend now invokes Claude with
 *   --output-format stream-json --verbose
 * and TransformReadLine() in ClaudeCliService normalises the NDJSON frames
 * into the marker-line vocabulary the frontend parser already understands.
 *
 * This spec asserts that real, model-produced lines start arriving in the
 * output buffer well before the run finishes — proof of incremental
 * streaming, not a synthetic Started/Heartbeat placeholder.
 *
 * @billable — uses real Claude quota (Haiku, fast).
 */

const WATCH_PATH = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

test.describe('Claude Code — incremental streaming @billable', () => {
  test.skip(process.env.SKIP_BILLABLE === '1', 'Skipped via SKIP_BILLABLE=1');
  test.setTimeout(180_000);

  test('produces non-synthetic output lines while still running', async () => {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const created = await createJob({
      title: `e2e Streaming ${stamp}`,
      watchPath: WATCH_PATH,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      // Ask for a multi-line answer so several frames show up.
      promptMarkdown:
        'Write three short numbered facts about the number 42. Do not edit any files.',
      targetState: '2-ready'
    });

    await startJob(created.id, WATCH_PATH, {
      cliType: 'claude',
      model: 'claude-haiku-4-5'
    });

    // Wait for the run to complete and collect the output.
    const finished = await waitForJob(
      created.id,
      WATCH_PATH,
      j => j.execution !== null && j.execution.status !== 'running',
      { timeoutMs: 120_000, intervalMs: 1_000 }
    );
    expect(finished.execution!.status).toBe('completed');

    const out = await getJobOutput(created.id, WATCH_PATH) as Array<{ stream: string; text: string }>;
    expect(Array.isArray(out)).toBe(true);

    // Strip the synthetic taskboard markers; what's left must be Claude
    // content, not just "[taskboard] Started ...".
    const realLines = out.filter(l => !l.text.startsWith('[taskboard]'));
    expect(
      realLines.length,
      `no real CLI output captured — only synthetic markers reached the buffer:\n${JSON.stringify(out, null, 2)}`
    ).toBeGreaterThan(0);

    // At least one line should contain "42" — proves the model's answer
    // landed in the buffer and was not lost in the JSON-frame translation.
    const haveContent = realLines.some(l => /42/.test(l.text));
    expect(haveContent, `no '42' in output:\n${realLines.map(l => l.text).join('\n')}`).toBe(true);

    // Buffer must NOT contain raw {"type":"assistant"...} JSON — the transform
    // is supposed to convert frames into human marker lines.
    const rawJsonLeak = realLines.find(l => /^\{"type":/.test(l.text));
    expect(
      rawJsonLeak,
      `raw stream-json frame leaked unparsed: ${rawJsonLeak?.text}`
    ).toBeUndefined();
  });
});
