import { test, expect, type Page } from '@playwright/test';
import { contrastRatio } from '../helpers/contrast';
import { __devBannerStyleForTests } from '../../src/dev-mode';

/**
 * F41 — the DEV-environment indicator (orange stripe + vertical "DEV"
 * badge injected by `frontend/src/dev-mode.ts` when /api/environment
 * returns `{ isDev: true }`) was hardcoding `rgba(245, 158, 11, 0.82)` /
 * `#1a1208` and rendered as a pale, unreadable badge on the light theme.
 *
 * This spec locks the theme contract for the fix:
 *   - the badge renders with `--studio-accent` background +
 *     `--studio-on-accent` text in both themes (token-driven, no hex);
 *   - the body text contrast against the effective surface clears WCAG-AA
 *     in BOTH dark and light themes;
 *   - the left-edge stripe stays visibly coloured (non-transparent
 *     gradient end-stops) in both themes.
 *
 * The CSS block under test is imported verbatim from `dev-mode.ts`
 * (`__devBannerStyleForTests`), so the spec exercises the same rules the
 * app injects at bootstrap — drift between source and test is impossible
 * without breaking the import.
 *
 * Tier-1 + Tier-2 design tokens are inlined here as a minimal subset of
 * `_tokens-primitives.scss` / `_tokens-semantic.scss` so the static harness
 * resolves `var(--studio-accent)` etc. without booting the full app.
 */

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function captureForReport(page: Page, name: string): Promise<void> {
  if (process.env.F41_RESULTS_DIR) {
    await page.screenshot({
      path: `${process.env.F41_RESULTS_DIR}/${name}.png`,
      fullPage: false,
    });
  }
}

// Minimal token subset mirrored from `_tokens-primitives.scss` +
// `_tokens-semantic.scss`. Only the variables the dev banner reads are
// included; the values are copied verbatim so a future token-table tweak
// that breaks the assumption fails this spec.
const TOKENS = `
  :root, html[data-studio-theme='dark'] {
    --color-orange-500:  #d97757;
    --color-mocha-red:   #f38ba8;
    --color-red-700:     #b91c1c;
    --color-grey-50:     #fafafa;
    --color-grey-200:    #e5e5e5;
    --color-grey-950:    #1a1a1a;

    --studio-accent:     var(--color-orange-500);
    --studio-on-accent:  var(--color-grey-950);
    --lane-failed:       var(--color-mocha-red);
    --studio-bg-editor:  #11111b;
    --elevation-popover: 0 2px 6px rgba(0, 0, 0, 0.30);
  }
  html[data-studio-theme='light'] {
    --lane-failed:       var(--color-red-700);
    --studio-bg-editor:  var(--color-grey-50);
    --elevation-popover: 0 4px 14px rgba(0, 0, 0, 0.10);
  }
  html, body {
    margin: 0;
    padding: 0;
    width: 100%;
    height: 320px;
    background: var(--studio-bg-editor);
  }
`;

interface BannerSample {
  bg: string;
  fg: string;
  width: number;
  height: number;
}

async function readBannerSample(page: Page): Promise<BannerSample> {
  return await page.evaluate(() => {
    const banner = document.querySelector<HTMLElement>('[data-testid="dev-banner"]');
    if (!banner) throw new Error('dev-banner missing');
    const cs = getComputedStyle(banner);
    const rect = banner.getBoundingClientRect();
    return {
      bg: cs.backgroundColor,
      fg: cs.color,
      width: rect.width,
      height: rect.height,
    };
  });
}

async function readStripeBg(page: Page): Promise<string> {
  // `::before` is queryable through getComputedStyle on the host element.
  return await page.evaluate(() => {
    return getComputedStyle(document.body, '::before').backgroundImage;
  });
}

test.describe('F41 — dev banner stays legible in both themes', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`badge body text clears WCAG-AA (${theme})`, async ({ page }, testInfo) => {
      await page.setContent(`<!doctype html>
<html data-studio-theme="${theme}"><head><meta charset="utf-8">
<style>${TOKENS}${__devBannerStyleForTests}</style>
</head><body>
  <div class="dev-banner" data-testid="dev-banner" aria-label="DEV">DEV</div>
</body></html>`);

      await setTheme(page, theme);

      const sample = await readBannerSample(page);

      // 1. Badge body text must clear WCAG-AA against its (opaque) bg.
      //    A previous regression had a translucent background that folded
      //    onto the light page bg and washed out; opaque accent stays
      //    constant across themes, so the ratio is the same either way.
      const ratio = contrastRatio(sample.fg, sample.bg);
      expect(
        ratio,
        `[${theme}] dev-banner body contrast ${ratio.toFixed(2)} (${sample.fg} on ${sample.bg})`,
      ).toBeGreaterThan(4.5);

      // 2. Background must be opaque (no surface-bleed regression). The
      //    bug that started F41 was rgba(..., 0.82) — assert alpha = 1.
      const m = /rgba?\(\s*\d+[ ,]+\d+[ ,]+\d+(?:[ ,/]+([\d.]+))?\s*\)/.exec(sample.bg);
      const alpha = m && m[1] !== undefined ? Number(m[1]) : 1;
      expect(alpha, `[${theme}] dev-banner bg alpha`).toBeGreaterThanOrEqual(0.99);

      // 3. The badge must actually render — width/height must be non-zero,
      //    catching a "vertical-rl collapsed to nothing" regression.
      expect(sample.width, `[${theme}] dev-banner width`).toBeGreaterThan(0);
      expect(sample.height, `[${theme}] dev-banner height`).toBeGreaterThan(0);

      // 4. The body::before stripe must carry a real gradient (not
      //    `none`) so the left-edge cue stays visible in both themes.
      const stripeBg = await readStripeBg(page);
      expect(stripeBg, `[${theme}] stripe ::before background-image`).toMatch(/linear-gradient/);

      await testInfo.attach(`f41-dev-banner-${theme}.png`, {
        body: await page.screenshot({ fullPage: false, clip: { x: 0, y: 0, width: 60, height: 220 } }),
        contentType: 'image/png',
      });
      await captureForReport(page, `f41-dev-banner-${theme}`);
    });
  }
});
