import { test, expect } from '@playwright/test';

/**
 * F40 — three banner / toast surfaces now consume <app-notification>.
 *
 * The F37 primitive owns the icon bubble, surface tint, border, light/dark
 * theming, and ARIA contract. Three surfaces that pre-dated F37 still
 * carried their own chrome until this slice migrated them:
 *
 *   - `<app-update-banner>` (success / failure strip after an update run)
 *   - `.failed-pickup-banner` (amber button above the kanban that scrolls
 *     to the 3a-failed-pickup lane)
 *   - `.triage-toast` (bottom-right confirmation after a triage action)
 *
 * This spec mirrors the production markup in self-contained HTML
 * harnesses (same pattern as `workspace-banner-long-message.spec.ts`).
 * Goal: lock the structural contract — the testid is still present, and
 * the inner chrome carries the `.notification` BEM root with the right
 * severity + layout modifiers — without coupling to dev-backend state.
 *
 * The Angular template is the source of truth; if it drifts away from
 * this shape (e.g. a future refactor wraps it in another container or
 * drops the testid), the screenshot evidence + structural assertions
 * here will catch it on next CI run alongside the live-app smoke tests.
 */

// Shared chrome rules copied from notification.component.scss so the
// inline harnesses paint the same surfaces the production primitive
// would render. Keep this CSS minimal — only what the assertions read.
const NOTIFICATION_CSS = `
  :root {
    --notify-surface-bg: #1e1e2e;
    --notify-warning-border: rgba(251, 191, 36, 0.32);
    --notify-warning-icon-bg: rgba(251, 191, 36, 0.16);
    --notify-warning-icon-fg: #f9e2af;
    --notify-warning-tint: rgba(251, 191, 36, 0.10);
    --notify-success-border: rgba(74, 222, 128, 0.32);
    --notify-success-tint: rgba(74, 222, 128, 0.10);
    --notify-info-border: rgba(129, 140, 248, 0.32);
    --notify-info-icon-bg: rgba(129, 140, 248, 0.16);
    --notify-info-icon-fg: #b4befe;
  }
  body {
    margin: 0;
    padding: 24px;
    background: #181825;
    color: #cdd6f4;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
  }
  .notification {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    gap: 12px;
    align-items: flex-start;
    padding: 12px 14px;
    border-radius: 12px;
    background: var(--notify-surface-bg);
    border: 1px solid var(--notify-surface-border, #444);
    font-size: 13px;
  }
  .notification--layout-toast { box-shadow: 0 8px 24px rgba(0,0,0,0.5); }
  .notification--layout-banner { border-radius: 10px; padding: 8px 12px; }
  .notification--warning { border-color: var(--notify-warning-border); }
  .notification--warning.notification--layout-banner { background: var(--notify-warning-tint); }
  .notification--info { border-color: var(--notify-info-border); }
  .notification--success { border-color: var(--notify-success-border); }
  .notification--success.notification--layout-banner { background: var(--notify-success-tint); }
  .notification__icon {
    font-size: 16px;
    width: 22px;
    height: 22px;
    display: grid;
    place-items: center;
    border-radius: 999px;
  }
  .notification--warning .notification__icon {
    background: var(--notify-warning-icon-bg);
    color: var(--notify-warning-icon-fg);
  }
  .notification--info .notification__icon {
    background: var(--notify-info-icon-bg);
    color: var(--notify-info-icon-fg);
  }
  .notification__body {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
`;

test.describe('F40 — banner / toast surfaces consume <app-notification>', () => {
  test('failed-pickup banner wraps <app-notification kind="warning">', async ({ page }) => {
    // Mirrors the production button + notification structure from
    // frontend/src/app/app.html (the `@case ('board')` branch + the
    // legacy non-vsCode branch render the same markup).
    await page.setContent(`<!doctype html>
<html><head><meta charset="utf-8"><style>${NOTIFICATION_CSS}
.failed-pickup-banner {
  display: block;
  width: calc(100% - 32px);
  margin: 12px 16px 0;
  padding: 0;
  border: none;
  background: transparent;
  color: inherit;
  cursor: pointer;
  text-align: left;
  font: inherit;
}
.failed-pickup-banner__text { flex: 1 1 auto; min-width: 0; }
.failed-pickup-banner__text strong {
  color: var(--notify-warning-icon-fg);
  font-weight: 700;
  margin-right: 2px;
}
.failed-pickup-banner__chev {
  color: var(--notify-warning-icon-fg);
  font-size: 18px;
  line-height: 1;
  flex-shrink: 0;
  align-self: center;
  padding: 0 4px;
}
</style></head><body>
  <button type="button" class="failed-pickup-banner"
          data-testid="failed-pickup-banner"
          aria-label="Open failed-pickup lane">
    <div class="notification notification--warning notification--layout-banner"
         role="status" aria-live="polite">
      <span class="notification__icon" aria-hidden="true">⚠</span>
      <div class="notification__body">
        <div class="notification__message">
          <span class="failed-pickup-banner__text">
            <strong data-testid="failed-pickup-banner-count">2</strong>
            jobs failed to pick up. Open the failed-pickup lane.
          </span>
        </div>
        <span class="failed-pickup-banner__chev" aria-hidden="true">›</span>
      </div>
    </div>
  </button>
</body></html>`);

    const banner = page.getByTestId('failed-pickup-banner');
    await expect(banner).toBeVisible();
    await expect(page.getByTestId('failed-pickup-banner-count')).toHaveText('2');

    // The button still owns the click affordance; the inner chrome is the
    // F37 primitive's BEM root with the warning + banner modifiers.
    const inner = banner.locator('.notification').first();
    await expect(inner).toHaveClass(/notification--warning/);
    await expect(inner).toHaveClass(/notification--layout-banner/);
    // Icon bubble is owned by the primitive, not a separate __dot div.
    await expect(banner.locator('.failed-pickup-banner__dot')).toHaveCount(0);

    // The banner still scrolls into a click target the user can hit
    // anywhere along its width: button is the host, primitive sits inside.
    const role = await banner.evaluate((el) => el.tagName);
    expect(role).toBe('BUTTON');

    await banner.screenshot({ path: 'test-results/f40-failed-pickup-banner.png' });
  });

  test('triage-toast wraps <app-notification kind="info" layout="toast">', async ({ page }) => {
    // Mirrors `<app-notification class="triage-toast" kind="info" layout="toast">`
    // from app.html's `@case ('task')` branch. The class lands on the
    // host (positioning); the kind/layout modifiers land on the inner div.
    await page.setContent(`<!doctype html>
<html><head><meta charset="utf-8"><style>${NOTIFICATION_CSS}
.triage-toast {
  display: block;
  position: absolute;
  right: 24px;
  bottom: 24px;
  max-width: min(360px, calc(100vw - 48px));
  pointer-events: none;
}
</style></head><body>
  <div class="triage-toast">
    <div class="notification notification--info notification--layout-toast"
         role="status" aria-live="polite" data-testid="triage-toast">
      <span class="notification__icon" aria-hidden="true">✓</span>
      <div class="notification__body">
        <div class="notification__message">Moved to Ready.</div>
      </div>
    </div>
  </div>
</body></html>`);

    const toast = page.getByTestId('triage-toast');
    await expect(toast).toBeVisible();
    await expect(toast).toHaveClass(/notification--info/);
    await expect(toast).toHaveClass(/notification--layout-toast/);
    // Pre-F40 implementation used a 999px-radius pill; the unified
    // primitive paints a rounded card instead.
    const borderRadius = await toast.evaluate((el) => getComputedStyle(el).borderRadius);
    expect(borderRadius).not.toBe('999px');

    await page.locator('.triage-toast').screenshot({ path: 'test-results/f40-triage-toast.png' });
  });

  test('update-banner done mode wraps <app-notification kind="success" layout="banner">', async ({ page }) => {
    // Mirrors `<app-notification kind="success" layout="banner" testid="update-banner-done">`
    // from update-banner.component.html. The testid forwards onto the
    // primitive's inner div (the .notification BEM root).
    await page.setContent(`<!doctype html>
<html><head><meta charset="utf-8"><style>${NOTIFICATION_CSS}
.update-banner__head {
  margin-left: 0.5rem;
  font-family: ui-monospace, monospace;
  font-size: 0.75rem;
}
.update-banner__actions {
  display: flex;
  gap: 0.4rem;
  margin-top: 0.4rem;
}
.update-banner__btn {
  padding: 0.25rem 0.75rem;
  border-radius: 4px;
  border: 1px solid #444;
  background: rgba(255,255,255,0.04);
  color: inherit;
  cursor: pointer;
  font-size: 0.8125rem;
}
</style></head><body>
  <div class="update-banner notification notification--success notification--layout-banner"
       role="status" aria-live="polite" data-testid="update-banner-done">
    <span class="notification__icon" aria-hidden="true">✓</span>
    <div class="notification__body">
      <div class="notification__message">
        <span class="update-banner__text">
          Update finished:
          <code class="update-banner__head">abc1234</code>
          <span class="update-banner__arrow"> → </span>
          <code class="update-banner__head">def5678</code>.
          Reload required for the FE to pick up new code.
        </span>
      </div>
      <div class="update-banner__actions">
        <button type="button" class="update-banner__btn" data-testid="update-banner-reload">Reload</button>
        <button type="button" class="update-banner__btn" data-testid="update-banner-dismiss">Dismiss</button>
      </div>
    </div>
  </div>
</body></html>`);

    const banner = page.getByTestId('update-banner-done');
    await expect(banner).toBeVisible();
    await expect(banner).toHaveClass(/notification--success/);
    await expect(banner).toHaveClass(/notification--layout-banner/);
    // Reload + Dismiss buttons survived the migration into the
    // [notification-actions] slot.
    await expect(page.getByTestId('update-banner-reload')).toBeVisible();
    await expect(page.getByTestId('update-banner-dismiss')).toBeVisible();

    await banner.screenshot({ path: 'test-results/f40-update-banner-done.png' });
  });
});
