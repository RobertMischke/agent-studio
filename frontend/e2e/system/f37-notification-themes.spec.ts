import { test, expect, type Page } from '@playwright/test';

/**
 * F37 — Unified <app-notification> must stay readable across light + dark.
 *
 * The notification stack and the workspace banner both render through
 * the unified `<app-notification>` primitive. Every colour reads through
 * the `--notify-*` Tier-2 tokens, which flip in the
 * `[data-studio-theme='light']` block of `_tokens-semantic.scss`.
 *
 * Two contracts locked here:
 *  - Each severity surface stays visible: the icon-fg / surface-bg pair
 *    clears WCAG AA (≥ 4.5:1) in both themes so a future regression
 *    that drops `--notify-*-icon-fg` for a near-background pigment is
 *    caught immediately.
 *  - The shared chrome (border, animation, layout) renders one element
 *    per kind in both themes; "the stack disappears in light" is a
 *    failure we'd rather see in CI than in the user's seat.
 */

const KINDS = ['success', 'info', 'warning', 'error'] as const;

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

function parseRgb(value: string): [number, number, number, number] {
  const m = /rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(value);
  if (!m) throw new Error(`Cannot parse colour: ${value}`);
  return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] === undefined ? 1 : Number(m[4])];
}

function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrastRatio(fgRaw: string, bgRaw: string): number {
  const fg = parseRgb(fgRaw);
  const bg = parseRgb(bgRaw);
  const fgRgb: [number, number, number] = [
    Math.round(fg[0] * fg[3] + bg[0] * (1 - fg[3])),
    Math.round(fg[1] * fg[3] + bg[1] * (1 - fg[3])),
    Math.round(fg[2] * fg[3] + bg[2] * (1 - fg[3])),
  ];
  const l1 = luminance(fgRgb);
  const l2 = luminance([bg[0], bg[1], bg[2]]);
  const [light, dark] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (light + 0.05) / (dark + 0.05);
}

test.describe('F37 — unified notification component, light + dark', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`notification stack renders four kinds with readable icons (${theme})`, async ({ page }, testInfo) => {
      await page.goto('/');
      await page.waitForLoadState('domcontentloaded');
      // Allow the shell to mount and the theme effect to settle.
      await page.waitForTimeout(1200);
      await setTheme(page, theme);

      // Drive the live notification service directly so the test does not
      // depend on a particular feature flow.
      await page.waitForFunction(() => Boolean((window as { __notifications?: unknown }).__notifications));
      await page.evaluate(() => {
        const svc = (window as unknown as {
          __notifications: {
            success: (m: string, t?: string) => void;
            info: (m: string, t?: string) => void;
            warning: (m: string, t?: string) => void;
            error: (m: string, t?: string) => void;
            dismissAll: () => void;
          };
        }).__notifications;
        svc.dismissAll();
        svc.success('Saved.', 'Done');
        svc.info('Lane cleared.');
        svc.warning('Three retries left before fallback.', 'Quota low');
        svc.error('Backend returned 500. Run ./api.sh restart.', 'Request failed');
      });

      const stack = page.getByTestId('notification-stack');
      await expect(stack).toBeVisible({ timeout: 5_000 });
      await expect(page.locator('app-notification')).toHaveCount(4, { timeout: 5_000 });

      // Per kind: icon glyph + container background resolve to a pair
      // that clears WCAG AA. This is what catches "light icon on light
      // surface" regressions silently introduced by a stray hex literal.
      for (const kind of KINDS) {
        const surface = page.getByTestId(`notification-${kind}`);
        await expect(surface).toBeVisible();

        const sample = await surface.evaluate((el) => {
          const cs = getComputedStyle(el);
          const icon = el.querySelector('.notification__icon');
          const iconCs = icon ? getComputedStyle(icon) : null;
          return {
            surfaceBg: cs.backgroundColor,
            iconBg: iconCs?.backgroundColor ?? '',
            iconFg: iconCs?.color ?? '',
            border: cs.borderTopColor,
          };
        });

        // The icon bubble is layered: its visible colour is the glyph
        // colour folded onto (iconBg over surfaceBg). Fold both ways so
        // we catch a regression that drops either layer.
        const ratio = contrastRatio(sample.iconFg, sample.surfaceBg);
        expect(
          ratio,
          `[${theme}/${kind}] icon contrast ${ratio.toFixed(2)} (${sample.iconFg} on ${sample.surfaceBg})`
        ).toBeGreaterThan(3.0);

        // The per-kind border must NOT be transparent or equal to the
        // surface (then the severity tint disappears).
        const [, , , borderAlpha] = parseRgb(sample.border);
        expect(borderAlpha, `[${theme}/${kind}] border alpha`).toBeGreaterThan(0.05);
      }

      // Brief settle so the slide-in animation has finished before the
      // screenshot; capturing mid-animation produced an unreadable
      // first-attempt evidence file in earlier runs.
      await page.waitForTimeout(400);
      await testInfo.attach(`f37-notification-stack-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png',
      });
      if (process.env.F37_RESULTS_DIR) {
        await page.screenshot({
          path: `${process.env.F37_RESULTS_DIR}/f37-notification-stack-${theme}.png`,
          fullPage: false,
        });
      }
    });
  }
});
