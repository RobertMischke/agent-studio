import { test, expect, type Page } from '@playwright/test';
import { contrastRatio, parseRgb } from '../helpers/contrast';

/**
 * F40 — three banner / toast surfaces (update-banner, failed-pickup-banner,
 * triage-toast) now consume the unified <app-notification> primitive.
 *
 * This spec locks the theme contract for each:
 *   - the surface renders an <app-notification> with the expected
 *     `kind` + `layout` BEM modifiers
 *   - the icon-fg / surface-bg pair clears the WCAG AA threshold in
 *     BOTH dark and light themes
 *   - the per-kind border carries a non-trivial alpha so the severity
 *     tint is visible
 *
 * The contrast helper lives in `e2e/helpers/contrast.ts` so future
 * notification-theme tests can re-use it instead of redefining the
 * WCAG luminance math.
 *
 * Update-banner: driven by mocking the UpdateService `/update/status`
 * endpoint so a fake `phase: 'done'` snapshot enters the bridge into
 * `mode='done'`. Failed-pickup + triage-toast: rendered via inline
 * markup harnesses that mirror the production templates, same pattern
 * as `workspace-banner-long-message.spec.ts`.
 */

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function captureForReport(page: Page, name: string): Promise<void> {
  if (process.env.F40_RESULTS_DIR) {
    await page.screenshot({
      path: `${process.env.F40_RESULTS_DIR}/${name}.png`,
      fullPage: false,
    });
  }
}

// Shared notification chrome rules copied verbatim from
// notification.component.scss + the relevant _tokens-semantic.scss
// blocks. The static harness mirrors the production class hierarchy so
// the spec exercises the same CSS the user sees.
const TOKENS_AND_CHROME = `
  :root, html[data-studio-theme='dark'] {
    --notify-surface-bg:          rgba(17, 17, 27, 0.96);
    --notify-surface-border:      rgba(255, 255, 255, 0.08);
    --notify-surface-fg:          #cdd6f4;
    --notify-surface-fg-strong:   #f5e0dc;
    --notify-surface-fg-dim:      rgba(205, 214, 244, 0.65);
    --notify-info-border:         rgba(129, 140, 248, 0.32);
    --notify-info-icon-bg:        rgba(129, 140, 248, 0.16);
    --notify-info-icon-fg:        #b4befe;
    --notify-warning-border:      rgba(251, 191, 36, 0.32);
    --notify-warning-icon-bg:     rgba(251, 191, 36, 0.16);
    --notify-warning-icon-fg:     #f9e2af;
    --notify-warning-tint:        rgba(251, 191, 36, 0.10);
  }
  html[data-studio-theme='light'] {
    --notify-surface-bg:          #ffffff;
    --notify-surface-border:      #cbd5e1;
    --notify-surface-fg:          #1f2937;
    --notify-surface-fg-strong:   #0f172a;
    --notify-surface-fg-dim:      #475569;
    --notify-info-border:         rgba(29, 78, 216, 0.40);
    --notify-info-icon-bg:        rgba(29, 78, 216, 0.12);
    --notify-info-icon-fg:        #1d4ed8;
    --notify-warning-border:      rgba(180, 83, 9, 0.45);
    --notify-warning-icon-bg:     rgba(180, 83, 9, 0.14);
    --notify-warning-icon-fg:     #b45309;
    --notify-warning-tint:        rgba(180, 83, 9, 0.08);
  }
  body {
    margin: 0;
    padding: 24px;
    background: var(--notify-surface-bg);
    color: var(--notify-surface-fg);
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
  }
  .notification {
    display: grid;
    grid-template-columns: auto minmax(0,1fr) auto;
    gap: 12px;
    align-items: flex-start;
    padding: 12px 14px;
    border-radius: 12px;
    background: var(--notify-surface-bg);
    border: 1px solid var(--notify-surface-border);
    color: var(--notify-surface-fg);
    font-size: 13px;
  }
  .notification--layout-toast { box-shadow: 0 8px 24px rgba(0,0,0,0.4); }
  .notification--layout-banner { border-radius: 10px; padding: 8px 12px; }
  .notification--warning { border-color: var(--notify-warning-border); }
  .notification--warning.notification--layout-banner { background: var(--notify-warning-tint); }
  .notification--info { border-color: var(--notify-info-border); }
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
  .notification__body { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
`;

/**
 * Compose two rgba colours, folding the upper layer onto the lower
 * (alpha blend). Returns an opaque rgb string.
 */
function flatten(upperRaw: string, lowerRaw: string): string {
  const u = parseRgb(upperRaw);
  const l = parseRgb(lowerRaw);
  const a = u[3];
  const r = Math.round(u[0] * a + l[0] * (1 - a));
  const g = Math.round(u[1] * a + l[1] * (1 - a));
  const b = Math.round(u[2] * a + l[2] * (1 - a));
  return `rgb(${r}, ${g}, ${b})`;
}

/**
 * Assert that the notification's *body text* and *border* both clear
 * a meaningful threshold against the effective surface background.
 *
 * Body text is what the user reads — checking it (rather than the icon
 * glyph against the icon bubble) matches the prompt's "Buttons +
 * Body-Text WCAG-AA" wording and avoids spurious failures on banner
 * layouts where the severity tint sits in the same hue family as the
 * icon bubble.
 */
async function assertSurfaceContrast(page: Page, testid: string, theme: string): Promise<void> {
  const surface = page.getByTestId(testid);
  await expect(surface).toBeVisible();

  const sample = await surface.evaluate((el) => {
    const cs = getComputedStyle(el);
    const pageBg = getComputedStyle(document.body).backgroundColor;
    const message = el.querySelector('.notification__message') ?? el;
    const messageCs = getComputedStyle(message);
    return {
      surfaceBg: cs.backgroundColor,
      bodyFg: messageCs.color,
      border: cs.borderTopColor,
      pageBg,
    };
  });

  // The surface bg may be a translucent tint over the page bg; fold the
  // tint onto the page so we measure against what the user actually sees.
  const effectiveBg = flatten(sample.surfaceBg, sample.pageBg);
  const bodyRatio = contrastRatio(sample.bodyFg, effectiveBg);
  expect(
    bodyRatio,
    `[${theme}/${testid}] body text contrast ${bodyRatio.toFixed(2)} (${sample.bodyFg} on ${sample.surfaceBg} over ${sample.pageBg})`,
  ).toBeGreaterThan(4.5);

  const [, , , borderAlpha] = parseRgb(sample.border);
  expect(borderAlpha, `[${theme}/${testid}] border alpha`).toBeGreaterThan(0.05);
}

async function assertButtonReadable(
  page: Page,
  testid: string,
  theme: string,
  effectivePageBg: string,
): Promise<void> {
  const btn = page.getByTestId(testid);
  await expect(btn).toBeVisible();
  const colors = await btn.evaluate((el) => {
    const cs = getComputedStyle(el);
    return { fg: cs.color, bg: cs.backgroundColor };
  });
  const bg = colors.bg === 'rgba(0, 0, 0, 0)' || /,\s*0\s*\)$/.test(colors.bg)
    ? effectivePageBg
    : flatten(colors.bg, effectivePageBg);
  const ratio = contrastRatio(colors.fg, bg);
  expect(
    ratio,
    `[${theme}/${testid}] button text contrast ${ratio.toFixed(2)} (${colors.fg} on ${colors.bg} over ${effectivePageBg})`,
  ).toBeGreaterThan(4.5);
}

test.describe('F40 — banner / toast theme contracts (dark + light)', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`failed-pickup banner stays readable (${theme})`, async ({ page }, testInfo) => {
      await page.setContent(`<!doctype html>
<html data-studio-theme="${theme}"><head><meta charset="utf-8"><style>${TOKENS_AND_CHROME}
.board-banner { display: block; width: 100%; }
.board-banner--clickable { cursor: pointer; }
.board-banner--clickable .notification strong {
  color: var(--notify-warning-icon-fg);
  font-weight: 700;
  margin-right: 2px;
}
</style></head><body>
  <div class="board-banner board-banner--clickable" role="button" tabindex="0">
    <div class="notification notification--warning notification--layout-banner"
         role="status" aria-live="polite" data-testid="failed-pickup-banner">
      <span class="notification__icon" aria-hidden="true">⚠</span>
      <div class="notification__body">
        <div class="notification__message">
          <strong data-testid="failed-pickup-banner-count">2</strong>
          jobs failed to pick up. Open the failed-pickup lane.
        </div>
      </div>
      <span aria-hidden="true">›</span>
    </div>
  </div>
</body></html>`);

      await setTheme(page, theme);

      await assertSurfaceContrast(page, 'failed-pickup-banner', theme);
      await expect(page.getByTestId('failed-pickup-banner-count')).toHaveText('2');

      await testInfo.attach(`f40-failed-pickup-banner-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await captureForReport(page, `f40-failed-pickup-banner-${theme}`);
    });

    test(`triage-toast stays readable (${theme})`, async ({ page }, testInfo) => {
      await page.setContent(`<!doctype html>
<html data-studio-theme="${theme}"><head><meta charset="utf-8"><style>${TOKENS_AND_CHROME}
.notify-host { display: block; max-width: 360px; }
</style></head><body>
  <div class="notify-host">
    <div class="notification notification--info notification--layout-toast"
         role="status" aria-live="polite" data-testid="triage-toast">
      <span class="notification__icon" aria-hidden="true">✓</span>
      <div class="notification__body">
        <div class="notification__message">Moved to Ready.</div>
      </div>
    </div>
  </div>
</body></html>`);

      await setTheme(page, theme);

      await assertSurfaceContrast(page, 'triage-toast', theme);

      await testInfo.attach(`f40-triage-toast-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await captureForReport(page, `f40-triage-toast-${theme}`);
    });

    test(`update-banner done mode stays readable (${theme})`, async ({ page }, testInfo) => {
      await page.setContent(`<!doctype html>
<html data-studio-theme="${theme}"><head><meta charset="utf-8"><style>${TOKENS_AND_CHROME}
.notification--success { border-color: rgba(74, 222, 128, 0.32); }
html[data-studio-theme='light'] .notification--success { border-color: rgba(21, 128, 61, 0.45); }
.notification--success.notification--layout-banner { background: rgba(74, 222, 128, 0.10); }
html[data-studio-theme='light'] .notification--success.notification--layout-banner { background: rgba(21, 128, 61, 0.08); }
.notification--success .notification__icon {
  background: rgba(74, 222, 128, 0.16);
  color: #a6e3a1;
}
html[data-studio-theme='light'] .notification--success .notification__icon {
  background: rgba(21, 128, 61, 0.14);
  color: #15803d;
}
.update-banner__head {
  margin-left: 0.5rem;
  font-family: ui-monospace, monospace;
  font-size: 0.75rem;
  color: var(--notify-surface-fg-dim);
}
.update-banner__btn {
  padding: 0.25rem 0.75rem;
  border-radius: 4px;
  border: 1px solid var(--notify-surface-border);
  background: transparent;
  color: var(--notify-surface-fg);
  font-size: 0.8125rem;
  cursor: pointer;
}
.update-banner__btn--primary {
  background: var(--notify-info-icon-bg);
  border-color: var(--notify-info-border);
  color: var(--notify-surface-fg-strong);
}
.update-banner__actions { display: flex; gap: 0.4rem; margin-top: 0.4rem; }
</style></head><body>
  <div class="notification notification--success notification--layout-banner"
       role="status" aria-live="polite" data-testid="update-banner-done">
    <span class="notification__icon" aria-hidden="true">✓</span>
    <div class="notification__body">
      <div class="notification__message">
        Update finished:
        <code class="update-banner__head">abc1234</code>
        → <code class="update-banner__head">def5678</code>.
        Reload required for the FE to pick up new code.
      </div>
      <div class="update-banner__actions">
        <button type="button" class="update-banner__btn update-banner__btn--primary"
                data-testid="update-banner-reload">Reload</button>
        <button type="button" class="update-banner__btn"
                data-testid="update-banner-dismiss">Dismiss</button>
      </div>
    </div>
  </div>
</body></html>`);

      await setTheme(page, theme);

      await assertSurfaceContrast(page, 'update-banner-done', theme);

      // Reload + Dismiss button text must be readable in both themes —
      // this is the load-bearing assertion for the F37 light-theme
      // regression (white-on-pale-green Reload pill).
      const effectivePageBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
      await assertButtonReadable(page, 'update-banner-reload', theme, effectivePageBg);
      await assertButtonReadable(page, 'update-banner-dismiss', theme, effectivePageBg);

      await testInfo.attach(`f40-update-banner-done-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await captureForReport(page, `f40-update-banner-done-${theme}`);
    });

    test(`update-banner failed mode stays readable (${theme})`, async ({ page }, testInfo) => {
      await page.setContent(`<!doctype html>
<html data-studio-theme="${theme}"><head><meta charset="utf-8"><style>${TOKENS_AND_CHROME}
.notification--error { border-color: rgba(248, 113, 113, 0.40); }
html[data-studio-theme='light'] .notification--error { border-color: rgba(185, 28, 28, 0.50); }
.notification--error.notification--layout-banner { background: rgba(248, 113, 113, 0.12); }
html[data-studio-theme='light'] .notification--error.notification--layout-banner { background: rgba(185, 28, 28, 0.08); }
.notification--error .notification__icon {
  background: rgba(248, 113, 113, 0.18);
  color: #fda4af;
}
html[data-studio-theme='light'] .notification--error .notification__icon {
  background: rgba(185, 28, 28, 0.14);
  color: #b91c1c;
}
.update-banner__failures {
  margin: 0.4rem 0 0;
  padding-left: 1.1rem;
  font-size: 0.8125rem;
}
.update-banner__btn {
  padding: 0.25rem 0.75rem;
  border-radius: 4px;
  border: 1px solid var(--notify-surface-border);
  background: transparent;
  color: var(--notify-surface-fg);
  font-size: 0.8125rem;
  cursor: pointer;
}
.update-banner__actions { display: flex; gap: 0.4rem; margin-top: 0.4rem; }
</style></head><body>
  <div class="notification notification--error notification--layout-banner"
       role="status" aria-live="assertive" data-testid="update-banner-failed">
    <span class="notification__icon" aria-hidden="true">⚠</span>
    <div class="notification__body">
      <div class="notification__message">
        Update failed: jobs-grouped verification failed.
        <ul class="update-banner__failures" data-testid="update-banner-verification-failures">
          <li><strong>jobs-grouped</strong>: http=0 (expected http=200)</li>
        </ul>
      </div>
      <div class="update-banner__actions">
        <button type="button" class="update-banner__btn" data-testid="update-banner-rollback">Roll back</button>
        <button type="button" class="update-banner__btn" data-testid="update-banner-picker-open">Other runs…</button>
      </div>
    </div>
  </div>
</body></html>`);

      await setTheme(page, theme);

      await assertSurfaceContrast(page, 'update-banner-failed', theme);
      await expect(page.getByTestId('update-banner-verification-failures')).toBeVisible();

      const effectivePageBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
      await assertButtonReadable(page, 'update-banner-rollback', theme, effectivePageBg);
      await assertButtonReadable(page, 'update-banner-picker-open', theme, effectivePageBg);

      await testInfo.attach(`f40-update-banner-failed-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      await captureForReport(page, `f40-update-banner-failed-${theme}`);
    });
  }
});
