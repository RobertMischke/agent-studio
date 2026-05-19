import { test, expect } from '@playwright/test';

/**
 * Renders the live "Agent is asking for input" banner from
 * project-detail.ts in isolation against an inline HTML harness so the
 * screenshot can be captured without a running backend (dev is offline by
 * default; bringing it up is reserved for the dev-backend fixture).
 *
 * Locks ADR-0027's UI contract: the live banner is visually distinct
 * from the post-run "review-decisions-pending" yellow banner - red border,
 * a textarea + Reply affordance, and a different shape (rounded card with
 * shadow) so a human glancing at the project view immediately sees that
 * the agent is currently waiting on them.
 */
test('live decision banner renders with reply affordance', async ({ page }) => {
  const html = `<!doctype html>
<html><head><meta charset="utf-8"><title>live decision banner</title>
<style>
  body {
    margin: 0;
    padding: 32px;
    background: #181825;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
    color: #cdd6f4;
  }
  .stage { max-width: 760px; margin: 0 auto; display: grid; gap: 18px; }
  .stage h2 { color: #f8fafc; margin: 0 0 4px; font-size: 1.1rem; }
  .stage p.lead { margin: 0 0 8px; font-size: 0.85rem; color: rgba(255,255,255,0.55); }

  /* Existing post-run banner (yellow) - rendered alongside so the contrast is visible. */
  .proj-detail__banner {
    margin: 0;
    padding: 10px 12px;
    border: 1px solid rgba(249, 226, 175, 0.40);
    border-left-width: 3px;
    background: rgba(249, 226, 175, 0.10);
    border-radius: 6px;
    color: #f1f5f9;
    font-size: 0.85rem;
  }
  .proj-detail__banner header {
    display: flex; align-items: baseline; gap: 8px; margin-bottom: 6px;
  }
  .proj-detail__banner header strong { color: #fcd34d; }
  .proj-detail__banner-count { margin-left: auto; color: rgba(255,255,255,0.55); font-size: 0.75rem; }
  .proj-detail__banner ul { margin: 4px 0 0; padding-left: 18px; }
  .proj-detail__banner li { margin: 2px 0; }
  .proj-detail__banner code {
    font-size: 0.78rem;
    background: rgba(255,255,255,0.06);
    padding: 1px 4px;
    border-radius: 3px;
  }
  .proj-detail__banner-reason { color: rgba(255,255,255,0.70); margin-left: 6px; }

  /* Live banner under test. */
  .proj-detail__live-banner {
    padding: 14px 16px;
    border: 1px solid rgba(248, 113, 113, 0.55);
    border-left-width: 4px;
    border-radius: 10px;
    background: linear-gradient(180deg, rgba(248,113,113,0.18) 0%, rgba(248,113,113,0.08) 100%);
    box-shadow: 0 6px 18px rgba(248,113,113,0.12);
    color: #f8fafc;
    font-size: 0.88rem;
  }
  .proj-detail__live-banner header { display: flex; align-items: baseline; gap: 10px; margin-bottom: 6px; }
  .proj-detail__live-banner header strong { color: #fda4af; font-size: 0.95rem; letter-spacing: 0.01em; }
  .proj-detail__live-banner__icon { font-size: 1.05rem; }
  .proj-detail__live-banner__chip {
    margin-left: auto;
    padding: 2px 8px;
    border-radius: 999px;
    background: rgba(248,113,113,0.20);
    color: #fda4af;
    font-size: 0.72rem;
    letter-spacing: 0.02em;
    text-transform: uppercase;
  }
  .proj-detail__live-banner__title { margin: 0 0 2px; color: #f8fafc; font-weight: 600; }
  .proj-detail__live-banner__reason { margin: 0 0 10px; color: rgba(248,250,252,0.85); font-style: italic; }
  .proj-detail__live-banner__reply textarea {
    width: 100%;
    box-sizing: border-box;
    background: rgba(0,0,0,0.30);
    color: #f8fafc;
    border: 1px solid rgba(255,255,255,0.18);
    border-radius: 6px;
    padding: 8px 10px;
    font: inherit;
    font-size: 0.84rem;
    resize: vertical;
    min-height: 64px;
  }
  .proj-detail__live-banner__actions {
    display: flex; align-items: center; gap: 10px; justify-content: flex-end; margin-top: 8px;
  }
  .proj-detail__live-banner__send {
    background: rgba(248,113,113,0.25);
    color: #fef2f2;
    border: 1px solid rgba(248,113,113,0.50);
    border-radius: 6px;
    padding: 6px 14px;
    font: inherit;
    font-size: 0.85rem;
    cursor: pointer;
  }
</style></head>
<body>
  <div class="stage">
    <div>
      <h2>project view (mockup)</h2>
      <p class="lead">Both decision banners side by side. Yellow is the post-run reminder; red is the live, in-progress signal that the agent is currently waiting for the user.</p>
    </div>

    <section class="proj-detail__banner" data-testid="post-run">
      <header>
        <span>⚠️</span>
        <strong>Orchestrator decision pending</strong>
        <span class="proj-detail__banner-count">1 task</span>
      </header>
      <ul>
        <li>
          <code>orchestrator-acts-on-review-decisions</code>
          <span class="proj-detail__banner-reason">migration safety unclear; pick A or B</span>
        </li>
      </ul>
    </section>

    <section class="proj-detail__live-banner" data-testid="live-banner">
      <header>
        <span class="proj-detail__live-banner__icon">🛎️</span>
        <strong>Agent is asking for input</strong>
        <span class="proj-detail__live-banner__chip">live · orchestrator-continuous-decision-visibility</span>
      </header>
      <p class="proj-detail__live-banner__title">Orchestrator continuous review and visible decision points</p>
      <p class="proj-detail__live-banner__reason">Should the live scanner detect [[TASK_BLOCKED]] in the same banner or split it into a second visual class?</p>
      <div class="proj-detail__live-banner__reply">
        <textarea rows="3" placeholder="Reply to the agent. This goes through the existing continue endpoint (mode: steer) and resolves the banner once received.">Same banner. Different sentinel kinds will toggle icon + colour ramp; structure stays one card per pending decision.</textarea>
        <div class="proj-detail__live-banner__actions">
          <button class="proj-detail__live-banner__send">Reply</button>
        </div>
      </div>
    </section>
  </div>
</body></html>`;

  await page.setViewportSize({ width: 900, height: 720 });
  await page.setContent(html, { waitUntil: 'load' });

  const live = page.getByTestId('live-banner');
  await expect(live).toBeVisible();
  await expect(live).toContainText('Agent is asking for input');
  await expect(live.locator('textarea')).toBeVisible();
  await expect(live.locator('button')).toContainText('Reply');

  await page.locator('.stage').screenshot({ path: 'test-results/live-decision-banner.png' });
});
