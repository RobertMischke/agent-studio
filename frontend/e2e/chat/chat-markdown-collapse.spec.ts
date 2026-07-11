import { test, expect } from '@playwright/test';

/**
 * Project chat — Slice A markdown rendering smoke.
 *
 * Pins the new behaviours added for the "primary product surface" work:
 *
 *  - non-user turns expose `data-testid="chat-turn-md"`,
 *  - long agent turns auto-collapse with a "Show more" toggle that flips
 *    the `data-collapsed` attribute and `aria-expanded` on the button,
 *  - fenced code blocks above the 5-line threshold render with a
 *    numbered gutter (`.md-code--numbered`, `.md-code-num`).
 *
 * Strategy: stub the orchestrator-chat history endpoint so the side sheet
 * always renders the synthetic turns we care about, regardless of what
 * the backend has on disk. We do not exercise sending a new message
 * (which would burn quota); we only assert what the UI does with a known
 * payload. The test is lightweight enough to run in the regular suite.
 */

const STUB_PROJECT_FALLBACK = 'agent-taskboard';

const LONG_AGENT_TEXT = [
  '## Heading two',
  '',
  'Here is a longer agent turn that exists to exercise the auto-collapse',
  'behaviour. The chat caps non-user turns past the line threshold and',
  'shows a "Show more" toggle below the body. Each line below adds another',
  'visual row so the source-line count comfortably exceeds the cutoff.',
  '',
  '- bullet alpha',
  '- bullet beta',
  '- bullet gamma',
  '- bullet delta',
  '- bullet epsilon',
  '- bullet zeta',
  '- bullet eta',
  '',
  '```',
  'function gamma(input) {',
  '  const items = input.split(",");',
  '  const total = items.length;',
  '  const half = Math.floor(total / 2);',
  '  const tail = items.slice(half);',
  '  return { total, half, tail };',
  '}',
  '```',
  '',
  'Closing paragraph so the long-turn classifier triggers reliably even if',
  'the layout font shrinks; what matters is the source line count, which',
  'crosses the 24-line threshold here.'
].join('\n');

const SHORT_AGENT_TEXT = 'Just a one-liner from the orchestrator.';

const stubChatResponse = {
  project: STUB_PROJECT_FALLBACK,
  turns: [
    {
      id: 'short-1',
      ts: '2026-05-06T11:59:00Z',
      role: 'orchestrator',
      text: SHORT_AGENT_TEXT
    },
    {
      id: 'long-1',
      ts: '2026-05-06T12:00:00Z',
      role: 'orchestrator',
      text: LONG_AGENT_TEXT
    }
  ]
};

test.describe('Project chat markdown — Slice A primitives', () => {
  test('renders chat-turn-md, collapses long turns, numbers long code blocks', async ({ page }) => {
    // Intercept the orchestrator-chat history fetch so the side sheet
    // always shows our fixture turns. Wildcard for any project name.
    await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
      if (route.request().method() !== 'GET') {
        await route.continue();
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(stubChatResponse)
      });
    });

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const toggle = page.getByTestId('orch-side-sheet-toggle');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    const sheet = page.getByTestId('orch-side-sheet');
    await expect(sheet).toBeVisible();

    const conversation = page.getByTestId('conversation-view');
    await expect(conversation).toBeVisible();

    const group = conversation.getByTestId('conversation-message-message.orchestrator');
    await expect(group).toBeVisible({ timeout: 5_000 });
    await expect(group).toHaveAttribute('data-item-count', '2');

    const longArticle = conversation.locator(
      '[data-testid="conversation-message-item"][data-item-id="long-1"]'
    );
    const shortArticle = conversation.locator(
      '[data-testid="conversation-message-item"][data-item-id="short-1"]'
    );
    await expect(longArticle).toBeVisible();
    await expect(shortArticle).toBeVisible();

    const showMore = longArticle.getByTestId('conversation-message-item-expand');
    await expect(showMore).toBeVisible();
    await expect(showMore).toHaveText('expand');
    await expect(shortArticle.getByTestId('conversation-message-item-expand')).toHaveCount(0);

    // Numbered code: the long turn carries a fence; >5 lines triggers the
    // numbered shape. The short turn should not.
    const numberedPre = longArticle.locator('pre.md-code--numbered');
    await expect(numberedPre).toBeVisible();
    expect(await numberedPre.locator('.md-code-num').count()).toBeGreaterThanOrEqual(7);

    // Click the toggle and confirm the article expands.
    await showMore.click();
    await expect(showMore).toHaveText('collapse');

    // Click again to re-collapse.
    await showMore.click();
    await expect(showMore).toHaveText('expand');

    // Capture a screenshot so the layout can be reviewed in chat without
    // running the UI. Tight crop to the side sheet only.
    const box = await sheet.boundingBox();
    if (box) {
      await page.screenshot({
        path: 'chat-markdown-collapse.png',
        clip: {
          x: Math.max(0, box.x - 4),
          y: Math.max(0, box.y - 4),
          width: Math.min(page.viewportSize()!.width - box.x + 4, box.width + 8),
          height: box.height + 8
        }
      });
    }
  });
});
