import { test, expect, Page } from '@playwright/test';

/**
 * Project-chat side-sheet: image attachments must render inline in the same
 * frame as the text bubble, and clicking the inline thumbnail must open the
 * shared media lightbox.
 *
 * Symptom before the fix: when the operator sent a message with an image
 * the bubble appeared with the text and the image popped in moments later
 * (the bubble was rendered from the local turn — text only — and the image
 * URL only landed after the server round-trip + an image fetch).
 *
 * Contract this spec locks down:
 *
 *   1. The `<img>` for the attached file is visible the moment the chat
 *      bubble is visible (same render pass). We arrange for the POST to
 *      the chat endpoint to be slow (1500 ms) so a "text now, image
 *      later" implementation would visibly fail the timing assertion;
 *      with the local-blob render the image is up in the same frame.
 *
 *   2. Clicking the inline thumbnail opens the shared MediaLightbox
 *      (the `<app-media-lightbox>` mounted at the app shell). Escape
 *      closes it. Backdrop click closes it.
 *
 * The chat history GET, upload POST, and chat send POST are all stubbed
 * so the spec runs without a live orchestrator session and without
 * burning CLI quota. We do not mock the attachment GET — the inline
 * source is a `blob:` URL pulled straight from the local file the
 * operator picked, so the network never needs to be involved for the
 * bubble to render.
 */

const SHOTS = 'screenshots/chat-attachment-inline-and-lightbox';

// 1×1 PNG, sufficient for the visibility checks; the file picker accepts it
// because its MIME type starts with image/.
const TINY_PNG_BASE64 =
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABh6FO1AAAAABJRU5ErkJggg==';

interface MockTurn {
  id: string;
  ts: string;
  role: 'user' | 'orchestrator';
  text: string;
  attachments?: { alt: string; relativePath: string }[] | null;
  errorMessage?: string | null;
}

interface MockState {
  turns: MockTurn[];
}

function nowIso(offsetMs = 0): string {
  return new Date(Date.now() + offsetMs).toISOString();
}

async function installChatMocks(page: Page, project: string): Promise<MockState> {
  const state: MockState = { turns: [] };

  // Attachment upload — accept the file, return a stable relative path.
  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat\/attachments$/, async (route) => {
    if (route.request().method() !== 'POST') {
      await route.continue();
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        fileName: 'mocked-1x1.png',
        relativePath: 'chat-attachments/mocked-1x1.png',
        url: '/api/runner/' + encodeURIComponent(project) + '/orchestrator-chat/attachments/mocked-1x1.png',
      }),
    });
  });

  // Persisted attachment GET — serve the same 1×1 PNG bytes so the image
  // preload promise resolves cleanly. We deliberately delay this so a
  // hypothetical implementation that waits for the server URL to load
  // before painting the bubble would visibly fail.
  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat\/attachments\/mocked-1x1\.png$/, async (route) => {
    await new Promise((r) => setTimeout(r, 600));
    await route.fulfill({
      status: 200,
      contentType: 'image/png',
      body: Buffer.from(TINY_PNG_BASE64, 'base64'),
    });
  });

  // Chat history GET + send POST.
  await page.route(/\/api\/runner\/[^/]+\/orchestrator-chat$/, async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project, turns: state.turns }),
      });
      return;
    }
    if (route.request().method() === 'POST') {
      const body = JSON.parse(route.request().postData() ?? '{}') as {
        text: string;
        attachments?: { alt: string; relativePath: string }[];
      };
      // 1.5s pause so the bubble that's already visible is *not* relying
      // on the server round-trip to obtain its image source.
      await new Promise((r) => setTimeout(r, 1500));
      const userTurn: MockTurn = {
        id: `srv-u-${Date.now()}`,
        ts: nowIso(),
        role: 'user',
        text: body.text,
        attachments: body.attachments ?? null,
      };
      const reply: MockTurn = {
        id: `srv-o-${Date.now()}`,
        ts: nowIso(1),
        role: 'orchestrator',
        text: 'thanks — got your screenshot.',
      };
      state.turns = [...state.turns, userTurn, reply];
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ project, reply }),
      });
      return;
    }
    await route.continue();
  });

  return state;
}

async function openSideSheet(page: Page): Promise<string | null> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(800);
  const toggle = page.getByTestId('orch-side-sheet-toggle');
  await expect(toggle).toBeVisible({ timeout: 10_000 });
  await toggle.click();
  await expect(page.getByTestId('orch-side-sheet')).toBeVisible();
  await page.waitForTimeout(400);
  const projectSelect = page.getByTestId('orch-side-sheet-project-select');
  const project = await projectSelect.inputValue();
  return project || null;
}

test.describe('Project chat — inline attachment render + lightbox', () => {
  test('image is visible in the bubble in the same frame as the text, lightbox opens on click', async ({ page }) => {
    const project = await openSideSheet(page);
    if (!project) {
      test.skip(true, 'No watched projects — chat surface not mounted');
      return;
    }
    await installChatMocks(page, project);

    // Stage the file via the hidden <input type="file"> the paperclip
    // button triggers. setInputFiles fires the same change event so the
    // addAttachment path is exercised end-to-end.
    const fileInput = page.locator('input.chat__file-input').first();
    await fileInput.setInputFiles({
      name: 'screenshot.png',
      mimeType: 'image/png',
      buffer: Buffer.from(TINY_PNG_BASE64, 'base64'),
    });
    await expect(page.getByTestId('chat-drafts')).toBeVisible({ timeout: 2_000 });

    const composer = page.getByTestId('chat-input');
    await composer.fill('here is the screenshot for the bubble');
    await page.getByTestId('chat-send').click();

    // The user bubble must appear immediately with text AND image. We
    // assert both within the same short window: if the implementation
    // shipped the bubble before the image, the image would not be
    // visible until the server round-trip (1.5s + image fetch 600ms).
    const userBubble = page
      .locator('[data-testid="chat-msg-user"]')
      .filter({ hasText: 'screenshot for the bubble' })
      .first();
    await expect(userBubble).toBeVisible({ timeout: 1_000 });

    const inlineImage = userBubble.locator('[data-testid="chat-msg-attachment-image"]').first();
    // Tight timeout — the image must be visible within 300 ms of the
    // bubble, well before the 1.5s POST or 600 ms GET could resolve.
    await expect(inlineImage).toBeVisible({ timeout: 300 });

    // Assert the inline image source is a blob URL (the local file the
    // user attached), not the eventual `/api/.../attachments/...` path.
    // This is the load-bearing detail: the bubble paints with bytes the
    // browser already has, so there's no fetch latency between text and
    // image.
    const initialSrc = await inlineImage.getAttribute('src');
    expect(initialSrc ?? '').toMatch(/^blob:/);

    // Capture the bubble-with-image screenshot — the operator should
    // see exactly this state during the network-busy window.
    const sheet = page.getByTestId('orch-side-sheet');
    const sheetBox = await sheet.boundingBox();
    if (sheetBox) {
      await page.screenshot({
        path: `${SHOTS}/01-bubble-with-image.png`,
        clip: {
          x: Math.max(0, sheetBox.x - 4),
          y: Math.max(0, sheetBox.y - 4),
          width: Math.min(page.viewportSize()!.width - sheetBox.x + 4, sheetBox.width + 8),
          height: sheetBox.height + 8,
        },
      });
    }

    // Click the thumbnail → the shared media lightbox opens.
    await inlineImage.click();
    const lightbox = page.getByTestId('media-lightbox');
    await expect(lightbox).toBeVisible({ timeout: 1_000 });
    await expect(page.getByTestId('media-lightbox-image')).toBeVisible();
    await page.screenshot({ path: `${SHOTS}/02-lightbox-open.png`, fullPage: false });

    // Escape dismisses the lightbox.
    await page.keyboard.press('Escape');
    await expect(lightbox).toHaveCount(0, { timeout: 1_000 });

    // Re-open and dismiss via the close button.
    await inlineImage.click();
    await expect(lightbox).toBeVisible({ timeout: 1_000 });
    await page.getByTestId('media-lightbox-close').click();
    await expect(lightbox).toHaveCount(0, { timeout: 1_000 });

    // Re-open and dismiss via backdrop click.
    await inlineImage.click();
    await expect(lightbox).toBeVisible({ timeout: 1_000 });
    // The backdrop is the dialog root; clicking near a corner avoids the
    // centered <figure> which stops propagation.
    const lbBox = await lightbox.boundingBox();
    if (lbBox) {
      await page.mouse.click(lbBox.x + 10, lbBox.y + 10);
    }
    await expect(lightbox).toHaveCount(0, { timeout: 1_000 });

    // Let the server round-trip finish so any background blob revoke +
    // turn refresh settles before the page tears down.
    await page.waitForResponse(
      (resp) =>
        resp.url().match(/\/api\/runner\/[^/]+\/orchestrator-chat$/) !== null &&
        resp.request().method() === 'POST',
      { timeout: 10_000 }
    ).catch(() => { /* tolerate */ });
  });
});
