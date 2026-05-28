import { test, expect } from '@playwright/test';
import { writeFile, readFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob, waitForJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * Regression spec for the Overview tab tokens / session cleanup.
 *
 * The original symptom: an Overview tab on a completed task showed
 * "No token data recorded yet" even though the agent had clearly run, and
 * a SESSION row exposed a raw session UUID with no actionable context.
 *
 * Fix:
 *   1. SESSION row is removed from Overview entirely (session-chain badge
 *      still lives on the protocol pane).
 *   2. Tokens block now falls back to the CLI agent's own footer
 *      (`lastUsage`) when no orchestrator-side LLM activity has been
 *      attributed to the job. The block stays empty only when neither
 *      source has anything; the empty wording then explains *why* based
 *      on lane state instead of a flat "No token data".
 */
test.describe('Overview tab — tokens fallback + session row removed', () => {
  test('SESSION row removed; lastUsage surfaces as Agent (CLI footer) block', async ({ page }) => {
    const watchPath = await pickWatchPath();
    // 1-preparation keeps the auto-mode runner off the folder so the
    // writeFile against job.json below is not racing the runner moving
    // the folder into 3-progress under us.
    const job = await createJob({
      title: `overview-tokens-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Overview tokens fallback test',
      targetState: '1-preparation',
    });

    try {
      // Plant a sessionName + a lastUsage footer + a completed-state job.json
      // so we exercise the "agent ran, no orchestrator activity" path that
      // the original bug screenshot showed. The scanner may take a tick to
      // see the new folder; waitForJob polls until /api/jobs/{id} resolves.
      const created = await waitForJob(job.id, watchPath, () => true, { timeoutMs: 15_000 });
      const jobJsonPath = join(created.folderPath, 'job.json');
      const raw = JSON.parse(await readFile(jobJsonPath, 'utf-8'));
      raw.sessionName = 'c705779a-aaaa-bbbb-cccc-ddddeeeeffff';
      raw.lastUsage = {
        at: new Date().toISOString(),
        tokens: '~14.2k tokens',
        changes: '5 files',
        requests: '8',
      };
      await writeFile(jobJsonPath, JSON.stringify(raw, null, 2));

      // Wait until /api/jobs/{id} reflects the new lastUsage before the
      // UI loads — the scanner cache invalidates on the writeFile, and
      // until it repopulates the GET returns the pre-write snapshot.
      await waitForJob(
        job.id,
        watchPath,
        (j: { lastUsage?: { tokens?: string | null } | null }) =>
          j.lastUsage?.tokens === '~14.2k tokens',
        { timeoutMs: 15_000 },
      );

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // Land on the Overview tab.
      // Wait for the detail panes to render before reaching for tabs.
      await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
      const overviewTab = page.getByTestId('prompt-tab-overview');
      await expect(overviewTab).toBeVisible({ timeout: 15_000 });
      await overviewTab.click();

      const overview = page.getByTestId('overview-tab');
      await expect(overview).toBeVisible();

      // (1) SESSION row is gone — the textContent of the Agent block must
      // not include a "Session" label any more.
      const agentBlock = page.getByTestId('overview-agent');
      await expect(agentBlock).toBeVisible();
      await expect(agentBlock).not.toContainText('Session');

      // (2) Agent (CLI footer) sub-block renders the lastUsage values
      // verbatim, including the unstructured tokens / requests / changes
      // strings the CLI produced.
      const agentTokens = page.getByTestId('overview-tokens-agent');
      await expect(agentTokens).toBeVisible();
      await expect(agentTokens).toContainText('Agent');
      await expect(agentTokens).toContainText('~14.2k tokens');
      await expect(agentTokens).toContainText('5 files');
      await expect(agentTokens).toContainText('8');

      // (3) The blanket "No token data recorded yet" empty state must NOT
      // be on screen — that was the original bug.
      await expect(page.getByTestId('overview-tokens-empty')).toHaveCount(0);

      await page.screenshot({
        path: 'test-results/overview-tokens-fallback.png',
        fullPage: false,
      });
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  // The Agent Work block reads from the per-job logs/ directory. Planting
  // fixture log lines via direct writeFile races the JobIndexCache that
  // FileSystemWatcher invalidates on every write under the job folder;
  // /agent-work-summary then transiently returns 404 because the scanner
  // is mid-repopulation. Component-level coverage for the same fields
  // lives in overview-pane.component.spec.ts (`agent-work block surfaces
  // call count + tool counts from the poll service`). Re-enable this
  // path once the cache exposes a "wait for index" handle.
  test.skip('Agent Work block replaces the raw SESSION row with call + tool counts', async ({ page }) => {
    const watchPath = await pickWatchPath();
    // 1-preparation keeps the runner off this folder so the logs we plant
    // are still here when the agent-work-summary endpoint reads them.
    const job = await createJob({
      title: `overview-agent-work-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Overview agent-work block test',
      targetState: '1-preparation',
    });

    try {
      const created = await waitForJob(job.id, watchPath, () => true, { timeoutMs: 15_000 });
      const logsDir = join(created.folderPath, 'logs');
      await mkdir(logsDir, { recursive: true });

      // Plant one start event + a handful of tool-call rows. The reader
      // tolerates BOM-prefixed first lines, so we write plain ASCII to
      // keep the test fixture obvious.
      const sessionEvents = [
        {
          Ts: new Date(Date.now() - 60_000).toISOString(),
          Kind: 'start',
          Cli: 'claude',
          InputSessionId: null,
          CapturedSessionId: 'sess-fixture-1',
          Resumed: false,
          Reason: null,
          HeadShaBefore: null,
          HeadShaAfter: null,
        },
      ];
      await writeFile(
        join(logsDir, 'session-events.jsonl'),
        sessionEvents.map(e => JSON.stringify(e)).join('\n') + '\n',
        'utf-8',
      );

      const baseTs = Date.now() - 45_000;
      const toolRows: object[] = [];
      // 4 Reads, 2 Edits, 1 Bash, with completed pairs so we exercise the
      // started-only counting rule.
      const recipe = [
        ...Array(4).fill('Read'),
        ...Array(2).fill('Edit'),
        'Bash',
      ];
      recipe.forEach((tool, i) => {
        toolRows.push({ ts: new Date(baseTs + i * 1000).toISOString(), kind: 'started',   tool, argument: `arg-${i}` });
        toolRows.push({ ts: new Date(baseTs + i * 1000 + 500).toISOString(), kind: 'completed', tool, isError: false, firstLine: '' });
      });
      await writeFile(
        join(logsDir, 'tool-calls.jsonl'),
        toolRows.map(e => JSON.stringify(e)).join('\n') + '\n',
        'utf-8',
      );

      // Sanity-check the endpoint directly so a backend-side regression
      // surfaces with a clear message before the UI assertion. The job
      // index cache briefly invalidates when we wrote files inside the
      // folder; retry a few times until the scanner sees the job again.
      type Summary = {
        calls: number;
        toolCalls: number;
        toolCounts: { tool: string; count: number }[];
      };
      let summary: Summary | null = null;
      for (let i = 0; i < 10; i++) {
        try {
          summary = await api<Summary>(
            `/api/jobs/${encodeURIComponent(job.id)}/agent-work-summary?watchPath=${encodeURIComponent(watchPath)}`,
          );
          break;
        } catch {
          await new Promise(r => setTimeout(r, 500));
        }
      }
      if (!summary) throw new Error('agent-work-summary never returned a 2xx');
      expect(summary.calls).toBe(1);
      expect(summary.toolCalls).toBe(7);
      expect(summary.toolCounts).toEqual([
        { tool: 'Read', count: 4 },
        { tool: 'Edit', count: 2 },
        { tool: 'Bash', count: 1 },
      ]);

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      // Wait for the detail panes to render before reaching for tabs.
      await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
      const overviewTab = page.getByTestId('prompt-tab-overview');
      await expect(overviewTab).toBeVisible({ timeout: 15_000 });
      await overviewTab.click();

      // The Agent Work section renders with the call count + tool tally.
      // The poll service syncs on detail mount; bound the wait so the
      // first poll has time to land.
      const agentWork = page.getByTestId('overview-agent-work');
      await expect(agentWork).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId('agent-work-calls')).toContainText('1');
      await expect(page.getByTestId('agent-work-tools')).toContainText('7');
      await expect(page.getByTestId('agent-work-tools')).toContainText('Read');
      await expect(page.getByTestId('agent-work-tools')).toContainText('4');

      await page.screenshot({
        path: 'test-results/overview-agent-work.png',
        fullPage: false,
      });
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });

  test('empty state is lane-specific (ready vs completed)', async ({ page }) => {
    const watchPath = await pickWatchPath();
    // 1-preparation is one of the lanes whose empty message asserts
    // "Run not started" — same wording as 2-ready, and the runner won't
    // grab it during the spec.
    const job = await createJob({
      title: `overview-tokens-empty-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Overview tokens empty wording test',
      targetState: '1-preparation',
    });

    try {
      // Wait for the scanner to see the new job before the UI fetch.
      await waitForJob(job.id, watchPath, () => true, { timeoutMs: 15_000 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      // Wait for the detail panes to render before reaching for tabs.
      await expect(page.getByTestId('detail-panes')).toBeVisible({ timeout: 15_000 });
      const overviewTab = page.getByTestId('prompt-tab-overview');
      await expect(overviewTab).toBeVisible({ timeout: 15_000 });
      await overviewTab.click();

      // Ready lane: "Run not started yet" wording, not the old flat message.
      const empty = page.getByTestId('overview-tokens-empty');
      await expect(empty).toBeVisible();
      await expect(empty).toContainText(/Run not started/i);
      await expect(empty).not.toContainText('No token data recorded yet.');
    } finally {
      await api(
        `/api/jobs/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' },
      );
    }
  });
});
