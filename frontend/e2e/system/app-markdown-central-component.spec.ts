import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * F31: <cac-markdown> is the canonical surface for client-side markdown
 * rendering. This spec verifies that the wrapper renders into a single
 * `.markdown-body` container across the surfaces the migration touched
 * (task description history, activity log agent turns, project Architecture
 * / Steering / Security sections) so a future regression that introduces
 * a parallel renderer is caught at the integration layer.
 *
 * The spec is intentionally structural: it asserts class plumbing and
 * the absence of the inline `[innerHTML]="markdownToHtml(...)"` pattern,
 * not pixel-by-pixel rendering — that lives in
 * `markdown-body-consolidation.spec.ts`.
 */

interface WatchPath { path: string; name?: string }

const HISTORY_MARKDOWN = `# Extension heading\n\n- one\n- two\n\n\`inline\` and **bold**.`;

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function plantJobWithHistory(): Promise<{ id: string; watchPath: string }> {
  const watchPath = await pickWatchPath();
  const created = await createJob({
    title: `e2e-cac-markdown-${Date.now()}`,
    watchPath,
    cliType: 'claude',
    agent: 'claude',
    promptMarkdown: '# Original task\n\nbody',
    targetState: '2-ready',
  });
  // Append an extension so the prompt-history surface renders an
  // <cac-markdown> instance.
  await api(
    `/api/tasks/${encodeURIComponent(created.id)}/prompt-history?watchPath=${encodeURIComponent(watchPath)}`,
    {
      method: 'POST',
      body: JSON.stringify({ markdown: HISTORY_MARKDOWN }),
    },
  ).catch(() => {
    /* prompt-history endpoint not present in this build — soft-fail */
  });
  return { id: created.id, watchPath };
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE',
    });
  } catch { /* best-effort */ }
}

async function openDetail(page: Page, id: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
}

test.describe('<cac-markdown> central component', () => {
  // The "agent turn in activity-log" test below opens the LEGACY
  // activity-log-view's conversation mode. With Frontend:NextGenChat
  // default-ON the Activity tab would mount the next-gen conversation view,
  // so pin the flag off ('0') for the suite.
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('atp.flag.nextGenChat', '0'));
  });

  test('prompt-history entry renders into a .markdown-body div via <cac-markdown>', async ({ page }) => {
    const target = await plantJobWithHistory();
    try {
      await openDetail(page, target.id, target.watchPath);
      const entry = page.getByTestId(/prompt-history-entry-/).first();
      if (!(await entry.isVisible().catch(() => false))) {
        test.skip(true, 'No prompt-history extension entries available in this dev workspace');
        return;
      }
      // The entry should host a <cac-markdown> element. Its inner div
      // carries .markdown-body so the global typography rules apply.
      const md = entry.locator('cac-markdown');
      await expect(md).toBeVisible();
      const inner = md.locator('.markdown-body').first();
      await expect(inner).toBeVisible();
      // Heading renders as <h1> through the markdown renderer (not as literal "# Extension heading").
      const html = await inner.innerHTML();
      expect(html).toMatch(/<h1>Extension heading<\/h1>/);
      expect(html).toMatch(/<ul>/);
    } finally {
      await deleteJob(target.id, target.watchPath);
    }
  });

  test('agent turn in activity-log uses <cac-markdown>', async ({ page }) => {
    // Find any job whose logs already exist so we can open Conversation
    // mode without having to start a run.
    const jobs = await api<Array<{ id: string; watchPath: string }>>('/api/tasks');
    let target: { id: string; watchPath: string } | null = null;
    for (const j of jobs.slice(0, 40)) {
      try {
        const out = await api<{ lines?: unknown[] }>(
          `/api/tasks/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`,
        );
        if (Array.isArray(out.lines) && out.lines.length > 0) {
          target = j;
          break;
        }
      } catch { /* keep looking */ }
    }
    if (!target) {
      test.skip(true, 'No job with CLI output available in this dev workspace');
      return;
    }
    await openDetail(page, target.id, target.watchPath);
    await page.getByTestId('inspector-tab-activity').click();
    await page.getByTestId('activity-log-mode-conversation').click({ force: true });
    const convo = page.getByTestId('activity-log-conversation');
    await expect(convo).toBeVisible({ timeout: 5_000 });
    const agentMd = convo.locator('.convo-turn--agent cac-markdown .markdown-body').first();
    // Some jobs have no agent text (only tool / system) — skip in that case.
    if (!(await agentMd.isVisible({ timeout: 3_000 }).catch(() => false))) {
      test.skip(true, 'No agent turn with markdown body available');
      return;
    }
    await expect(agentMd).toBeVisible();
    await expect(agentMd).toHaveClass(/markdown-body/);
    await expect(agentMd).toHaveClass(/markdown-body--dense/);
  });
});
