import { expect, test, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { installFrontendOverride } from '../helpers/frontend-override';
import { setTheme } from '../helpers/theme';

/**
 * Regression for the live Orchestrator transcript's 4px scrollbar jitter.
 *
 * The transport projection already tracks every event by its durable turn id.
 * The unstable class was the mixed-height message row at the virtual-window
 * boundary: short turns, wrapped Markdown, and rows with metadata were all
 * represented by the same 120px spacer estimate. Every appended turn removed
 * a real DOM row, changed the spacer by 120px, and made sticky-to-bottom issue
 * a corrective scroll after layout. The result was a moving scrollbar thumb
 * and selective row flicker even though most tracked rows stayed mounted.
 *
 * This spec accelerates the normal 30-second silent transcript poll, appends
 * deterministic mixed-height turns, and observes the real DOM while the live
 * tail advances. The Orchestrator host must remain the sole scroll container,
 * must not create virtual spacers, and must not remove existing tracked rows.
 */

interface StubTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
}

interface ScrollProbe {
  removedRowIds: string[];
  samples: { top: number; height: number }[];
}

const INITIAL_TURNS = 72;
const FINAL_TURNS = 80;
const PROJECT = 'scroll-stability';

function buildTurns(): StubTurn[] {
  const base = Date.parse('2026-07-22T10:00:00Z');
  return Array.from({ length: FINAL_TURNS }, (_, index) => ({
    id: `jitter-turn-${index}`,
    ts: new Date(base + index * 1_000).toISOString(),
    role: index % 2 === 0 ? 'user' : 'orchestrator',
    text: index % 4 === 1
      ? `Detailed streamed turn ${index}\n\n- first measured line\n- second measured line\n- metadata-sized tail`
      : `Short streamed turn ${index}`,
  }));
}

async function stubGrowingTranscript(page: Page): Promise<() => number> {
  const turns = buildTurns();
  let reads = 0;

  await installFrontendOverride(page);

  // Keep the rendering regression independent of the operator's auth profile
  // and current board. More specific route handlers registered below win over
  // this shell fixture.
  await page.route(/\/api\//, route => {
    const requestPath = new URL(route.request().url()).pathname;
    let body = '{}';
    if (requestPath === '/api/auth/status') {
      body = JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    } else if (requestPath === '/api/watch-paths') {
      body = JSON.stringify([{ name: PROJECT, path: `/tmp/${PROJECT}`, rootPath: `/tmp/${PROJECT}` }]);
    } else if (requestPath === '/api/workspaces') {
      body = JSON.stringify([{
        id: 'workspace-1', displayName: 'Scroll fixture', sortOrder: 0, isDefault: true,
        projects: [{ id: PROJECT, displayName: PROJECT, shortCode: 'SS', workspaceId: 'workspace-1',
          storageLocation: `/tmp/${PROJECT}`, archived: false, urls: [] }],
      }]);
    } else if (requestPath === '/api/tasks/archive') {
      body = JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 });
    } else if (requestPath === '/api/tasks') {
      body = '[]';
    } else if (requestPath === '/api/tasks/grouped') {
      body = JSON.stringify({ backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], autoReview: [], humanReview: [], review: [], completed: [], archive: [] });
    } else if (requestPath === '/api/orchestrator/sessions') {
      body = JSON.stringify({ sessions: [] });
    } else if (/\/api\/(?:tags|clients|workspaces\/[^/]+\/tags)\/?$/.test(requestPath)) {
      body = '[]';
    } else if (requestPath === '/api/runner/status') {
      body = JSON.stringify({ projects: {} });
    } else if (requestPath === '/api/cli/quota') {
      body = JSON.stringify({ snapshots: [] });
    } else if (/\/api\/cli\/(?:codex|claude|gemini)\/models$/.test(requestPath)) {
      body = JSON.stringify({ models: [], source: 'scroll-fixture' });
    } else if (requestPath.startsWith('/api/bus/')) {
      body = '[]';
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body });
  });

  // Accelerate only the side sheet's documented 30-second silent poll. Other
  // application timers keep their production cadence.
  await page.addInitScript(() => {
    const nativeSetInterval = window.setInterval.bind(window);
    window.setInterval = ((handler: TimerHandler, timeout?: number, ...args: unknown[]) =>
      nativeSetInterval(handler, timeout === 30_000 ? 80 : timeout, ...args)) as typeof window.setInterval;
  });

  await page.route(/\/api\/runner\/[^/]+(?:\/[^/]+)?\/orchestrator-chat$/, async route => {
    if (route.request().method() !== 'GET') {
      await route.fallback();
      return;
    }
    const visibleCount = Math.min(FINAL_TURNS, INITIAL_TURNS + reads);
    reads += 1;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ project: PROJECT, turns: turns.slice(0, visibleCount) }),
    });
  });

  return () => reads;
}

async function openChat(page: Page) {
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.getByTestId('orch-side-sheet-toggle').click();
  const conversation = page.getByTestId('orchestrator-conversation');
  await expect(conversation).toBeVisible();
  await expect(page.getByText('Short streamed turn 70')).toBeVisible();
  return conversation;
}

async function installProbe(page: Page): Promise<void> {
  await page.getByTestId('orchestrator-conversation').evaluate(scroller => {
    const probe: ScrollProbe = { removedRowIds: [], samples: [] };
    (window as typeof window & { __orchestratorScrollProbe?: ScrollProbe })
      .__orchestratorScrollProbe = probe;

    const rowId = (node: Node): string | null => {
      if (!(node instanceof HTMLElement)) return null;
      const row = node.matches('[data-testid^="conversation-message-"]')
        ? node
        : node.querySelector<HTMLElement>('[data-testid^="conversation-message-"]');
      return row?.textContent?.match(/(?:Short|Detailed) streamed turn \d+/)?.[0] ?? null;
    };
    new MutationObserver(records => {
      for (const record of records) {
        for (const node of record.removedNodes) {
          const id = rowId(node);
          if (id) probe.removedRowIds.push(id);
        }
      }
    }).observe(scroller, { childList: true, subtree: true });

    const sample = () => probe.samples.push({
      top: (scroller as HTMLElement).scrollTop,
      height: (scroller as HTMLElement).scrollHeight,
    });
    (scroller as HTMLElement).addEventListener('scroll', sample, { passive: true });
    sample();
  });
}

test('mixed-height streamed rows stay mounted without scrollbar correction', async ({ page }, testInfo) => {
  const readCount = await stubGrowingTranscript(page);
  const conversation = await openChat(page);
  await installProbe(page);

  await expect.poll(readCount, { message: 'accelerated transcript poll count' })
    .toBeGreaterThanOrEqual(FINAL_TURNS - INITIAL_TURNS + 1);
  await expect(page.getByText('Detailed streamed turn 77')).toBeVisible();

  const snapshot = await conversation.evaluate(scroller => {
    const probe = (window as typeof window & { __orchestratorScrollProbe?: ScrollProbe })
      .__orchestratorScrollProbe ?? { removedRowIds: [], samples: [] };
    const style = getComputedStyle(scroller);
    const view = scroller.querySelector<HTMLElement>('[data-testid="conversation-view"]');
    const backwards = probe.samples.slice(1).map((sample, index) =>
      probe.samples[index].top - sample.top);
    return {
      overflowY: style.overflowY,
      conversationOverflowY: view ? getComputedStyle(view).overflowY : null,
      topSpacers: scroller.querySelectorAll('[data-testid="conversation-spacer-top"]').length,
      bottomSpacers: scroller.querySelectorAll('[data-testid="conversation-spacer-bottom"]').length,
      removedRowIds: probe.removedRowIds,
      maxBackwardScroll: Math.max(0, ...backwards),
      distanceFromBottom: scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight,
    };
  });

  const probePath = testInfo.outputPath('scroll-probe.json');
  await writeFile(probePath, JSON.stringify(snapshot, null, 2), 'utf8');
  await testInfo.attach('scroll-probe.json', {
    path: probePath,
    contentType: 'application/json',
  });

  expect(snapshot.topSpacers).toBe(0);
  expect(snapshot.bottomSpacers).toBe(0);
  expect(snapshot.overflowY).toBe('auto');
  expect(snapshot.conversationOverflowY).not.toMatch(/auto|scroll/);
  expect(snapshot.removedRowIds).toEqual([]);
  expect(snapshot.maxBackwardScroll).toBeLessThanOrEqual(1);
  expect(snapshot.distanceFromBottom).toBeLessThanOrEqual(24);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    const screenshotPath = testInfo.outputPath(`orchestrator-stream-stable-${theme}.png`);
    await page.screenshot({ path: screenshotPath });
    await testInfo.attach(`orchestrator-stream-stable-${theme}.png`, {
      path: screenshotPath,
      contentType: 'image/png',
    });
  }
});
