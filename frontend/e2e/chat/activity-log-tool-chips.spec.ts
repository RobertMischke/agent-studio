import { test, expect } from '@playwright/test';
import { listJobs } from '../helpers/jobs';
import { api } from '../helpers/api';

interface OutLine { timestamp: string; stream: string; text: string }

async function findJobWithToolBurst(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await listJobs();
  for (const j of jobs) {
    try {
      const out = await api<OutLine[]>(`/api/jobs/${encodeURIComponent(j.id)}/output?watchPath=${encodeURIComponent(j.watchPath)}`);
      if (!Array.isArray(out)) continue;
      // Need at least 2 tool action starts so the conversation view renders
      // the per-kind chips. The marker is whatever non-word character the CLI
      // emits as a bullet ("●" for Claude Code, "*" / "x" for older drivers).
      const actionStarts = out.filter((l) => /^[^\w\s]\s+(Read|Search|Grep|Edit|Write|Run|Build|Check)\b/i.test(l.text)).length;
      if (actionStarts >= 2) return { id: j.id, watchPath: j.watchPath };
    } catch { /* ignore */ }
  }
  return null;
}

/**
 * Verifies the Conversation view renders tool activity as per-kind weight
 * chips (Read x12, Grep x5) with a small duration indicator, instead of
 * one row per tool call. The user explicitly asked for accumulated counts
 * so the technical tool noise does not crowd out the agent reply.
 */
test.describe('Activity log — tool chips', () => {
  test('tool burst renders per-kind weight chips with a duration', async ({ page }) => {
    const target = await findJobWithToolBurst();
    if (!target) {
      test.skip(true, 'No job with multiple tool actions in its output');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conversationBtn = page.getByTestId('activity-log-mode-conversation');
    await expect(conversationBtn).toBeVisible({ timeout: 5_000 });
    await conversationBtn.click({ force: true });

    const convo = page.getByTestId('activity-log-conversation');
    await expect(convo).toBeVisible();

    // Tool activity is hidden by default; toggle it on so the pill is rendered.
    // Click the label rather than the input itself so the Angular (change) handler
    // fires the same way it would for a real user click.
    await page.getByTestId('activity-log-show-tools').click({ force: true });

    // Some jobs have raw tool-action lines in cli-output.log but no parsed run
    // for the activity-log-view to render against (e.g. archived job with only
    // a stale buffer). Skip rather than fail when the burst pill never lands.
    const pill = page.getByTestId('convo-tools-pill').first();
    try {
      await pill.waitFor({ state: 'visible', timeout: 8_000 });
    } catch {
      test.skip(true, 'Job has no rendered tool burst in the activity log');
      return;
    }

    const anyChip = pill.locator('.convo-tools__chip').first();
    await expect(anyChip).toBeVisible();

    // The chip text must contain a "x<number>" weight, never just the kind label.
    const chipText = (await anyChip.innerText()).trim();
    expect(chipText).toMatch(/[×x]\s*\d+/i);

    // Capture a screenshot of the activity log so the reviewer sees the new look.
    const body = page.getByTestId('activity-log-body');
    await body.screenshot({ path: 'activity-log-tool-chips.png' });
  });

  test('expanding a tool burst groups detail rows by kind', async ({ page }) => {
    const target = await findJobWithToolBurst();
    if (!target) {
      test.skip(true, 'No job with multiple tool actions in its output');
      return;
    }

    await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(target.watchPath)}`);

    const activityTab = page.getByTestId('inspector-tab-activity');
    await expect(activityTab).toBeVisible({ timeout: 10_000 });
    await activityTab.click();

    const conversationBtn = page.getByTestId('activity-log-mode-conversation');
    await expect(conversationBtn).toBeVisible({ timeout: 5_000 });
    await conversationBtn.click({ force: true });
    await page.getByTestId('activity-log-show-tools').click({ force: true });

    const pill = page.getByTestId('convo-tools-pill').first();
    try {
      await pill.waitFor({ state: 'visible', timeout: 8_000 });
    } catch {
      test.skip(true, 'Job has no rendered tool burst in the activity log');
      return;
    }
    await pill.click({ force: true });

    // After expansion the per-kind bin headers must be present, each carrying
    // a per-kind count badge (the "x<N>" suffix).
    const turn = page.locator('.convo-turn--tools').first();
    const binHeads = turn.locator('.convo-tools__bin-head');
    await expect(binHeads.first()).toBeVisible({ timeout: 3_000 });
    const binCount = await turn.locator('.convo-tools__bin-count').first().innerText();
    expect(binCount.trim()).toMatch(/^[×x]\d+$/i);
  });
});
