import { test, expect, type Page } from '@playwright/test';

/**
 * Workforce chat — role attribution + compressed summary layer.
 *
 * Pins the prompt's acceptance contract:
 *   - Every agent message in the project chat carries a recognizable
 *     role badge (Task Executor / Code Reviewer / Architecture Custodian
 *     / Security Auditor / Plan Curator / ...).
 *   - The badge exposes its description via a plain `title` attribute
 *     (no custom HTML tooltip).
 *   - The compressed summary layer renders one row per phase with a
 *     collapsed older phase and an expanded newest phase by default.
 *   - Clicking a collapsed row expands it (and vice versa).
 *   - An unknown / future role still renders an `agent-generic` badge
 *     instead of crashing.
 *
 * Uses route stubs against `/api/projects/{project}/chat/*` so the spec
 * does not depend on real chat history and can run from stable without
 * Playwright bringing the dev backend up.
 */

const PROJECT = 'demo-project';
const SHOTS = 'screenshots/workforce-chat-role-attribution';

interface FixtureTurn {
  turnId: string;
  author: string;
  kind: string;
  ts: string;
  body: string;
  refs?: string[];
}

/**
 * A small workforce conversation: a user steer, a Task Executor reply,
 * a Code Reviewer comment (via the `aspect:code-quality` ref), an
 * Architecture Custodian flag (via `role:architecture-custodian`),
 * another user steer, a Security Auditor follow-up, and a row from an
 * unknown future role so we can pin the fallback.
 */
function buildFixture(): FixtureTurn[] {
  const t = (offsetMin: number) =>
    new Date(Date.UTC(2026, 4, 11, 10, offsetMin, 0)).toISOString();
  return [
    {
      turnId: 'u-1',
      author: 'user',
      kind: 'turn',
      ts: t(0),
      body: 'Please add a phase-aware watchdog hint.',
    },
    {
      turnId: 'agent-1',
      author: 'claude',
      kind: 'turn',
      ts: t(1),
      body: 'Edited `PhaseAwareWatchdog.cs`, added the hint after the tool-burst window.',
    },
    {
      turnId: 'review-1',
      author: 'claude',
      kind: 'turn',
      ts: t(2),
      body: 'Style-wise this is fine; the new hint string lives next to FormatBudgetReason.',
      refs: ['aspect:code-quality'],
    },
    {
      turnId: 'arch-1',
      author: 'agent',
      kind: 'turn',
      ts: t(3),
      body: 'No layering drift: the watchdog still observes, never decides.',
      refs: ['role:architecture-custodian'],
    },
    {
      turnId: 'u-2',
      author: 'user',
      kind: 'turn',
      ts: t(10),
      body: 'Anything sensitive in the new branch?',
    },
    {
      turnId: 'sec-1',
      author: 'codex',
      kind: 'turn',
      ts: t(11),
      body: 'No new input surfaces; no secrets in the diff.',
      refs: ['role:security-auditor'],
    },
    {
      turnId: 'future-1',
      author: 'martian-cli',
      kind: 'turn',
      ts: t(12),
      body: 'Hello from a CLI the renderer has never heard of.',
    },
  ];
}

const FIXTURE = buildFixture();

async function stubProjectChat(page: Page): Promise<void> {
  await page.route(/\/api\/projects\/[^/]+\/chat\/scroll/, async (route) => {
    const url = new URL(route.request().url());
    const before = url.searchParams.get('before');
    const after = url.searchParams.get('after');
    const limit = Math.min(parseInt(url.searchParams.get('limit') ?? '50', 10), 200);
    const sorted = [...FIXTURE].sort((a, b) => a.ts.localeCompare(b.ts));
    let page$: FixtureTurn[];
    let direction: 'before' | 'after' | 'tail';
    if (before) {
      direction = 'before';
      page$ = sorted.filter((t) => t.ts < before).slice(-limit).reverse();
    } else if (after) {
      direction = 'after';
      page$ = sorted.filter((t) => t.ts > after).slice(0, limit);
    } else {
      direction = 'tail';
      page$ = sorted.slice(-limit).reverse();
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, direction, turns: page$ }),
    });
  });

  await page.route(/\/api\/projects\/[^/]+\/chat\/search/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, results: [] }),
    });
  });

  await page.route(/\/api\/projects\/[^/]+\/chat\/stats/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        totalCount: FIXTURE.length,
        oldestTs: FIXTURE[0].ts,
        newestTs: FIXTURE[FIXTURE.length - 1].ts,
      }),
    });
  });

  await page.route(/\/api\/projects\/[^/]+\/chat\/turn\/[^?]+/, async (route) => {
    const url = new URL(route.request().url());
    const m = url.pathname.match(/\/turn\/([^/]+)$/);
    const id = m ? decodeURIComponent(m[1]) : '';
    const found = FIXTURE.find((t) => t.turnId === id);
    if (!found) {
      await route.fulfill({ status: 404, body: '{}' });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, turn: found }),
    });
  });
}

async function openVirtualChat(page: Page): Promise<void> {
  await page.goto('/?virtualChat=1');
  await page.waitForLoadState('domcontentloaded');

  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();

  const list = page.getByTestId('project-chat-list');
  if (!(await list.count())) {
    test.skip(true, 'No watched projects available — virtual chat list cannot mount');
  }
  await expect(list).toBeVisible();
  await page.waitForResponse(/\/api\/projects\/[^/]+\/chat\/scroll/);
  await page.waitForTimeout(300);
}

test.describe('Workforce chat — role attribution + compressed summary', () => {
  test('every agent turn carries a recognizable role badge with a plain-text title tooltip', async ({ page }) => {
    await stubProjectChat(page);
    await openVirtualChat(page);

    // Each agent message has a role badge. The badge is identified by
    // its data-role-id; we walk the loaded turns and assert the badge
    // matches the expected role per the deterministic mapping.
    const expectations: Array<[string, string]> = [
      ['agent-1', 'task-executor'],
      ['review-1', 'code-reviewer'],
      ['arch-1', 'architecture-custodian'],
      ['sec-1', 'security-auditor'],
      ['future-1', 'agent-generic'],
    ];
    for (const [turnId, roleId] of expectations) {
      const turn = page.locator(`[data-turnid="${turnId}"]`);
      await expect(turn, `turn ${turnId} should render`).toBeVisible();
      const badge = turn.locator(`[data-role-id="${roleId}"]`);
      await expect(badge, `turn ${turnId} should carry the ${roleId} badge`).toBeVisible();
      const title = await badge.getAttribute('title');
      expect(title, `${roleId} title must be plain text`).toBeTruthy();
      expect(title?.includes('<')).toBe(false);
      expect(title?.includes('>')).toBe(false);
    }

    await page.screenshot({ path: `${SHOTS}/01-badges-1280.png` });
  });

  test('compressed summary layer renders one row per phase with newest expanded', async ({ page }) => {
    await stubProjectChat(page);
    await openVirtualChat(page);

    const summary = page.getByTestId('phase-summary-list');
    await expect(summary).toBeVisible();
    const rows = summary.getByTestId('phase-summary-row');
    const count = await rows.count();
    expect(count).toBe(2);

    // Newest expanded, older collapsed.
    await expect(rows.nth(0)).toHaveAttribute('data-expanded', 'false');
    await expect(rows.nth(1)).toHaveAttribute('data-expanded', 'true');
  });

  test('clicking a collapsed phase expands it', async ({ page }) => {
    await stubProjectChat(page);
    await openVirtualChat(page);

    const rows = page.getByTestId('phase-summary-row');
    const collapsed = rows.nth(0);
    await expect(collapsed).toHaveAttribute('data-expanded', 'false');
    await collapsed.locator('button').click();
    await expect(collapsed).toHaveAttribute('data-expanded', 'true');

    await page.screenshot({ path: `${SHOTS}/02-phase-expanded.png` });
  });

  test('an unknown role renders the agent-generic fallback without crashing', async ({ page }) => {
    await stubProjectChat(page);
    await openVirtualChat(page);

    const fallback = page.locator('[data-turnid="future-1"] [data-role-id="agent-generic"]');
    await expect(fallback).toBeVisible();
    const title = await fallback.getAttribute('title');
    expect(title).toBeTruthy();
  });
});
