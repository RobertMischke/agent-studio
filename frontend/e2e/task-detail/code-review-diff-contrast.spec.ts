import { test, expect, type Page } from '@playwright/test';

/**
 * F53 — Diff-view contrast for added/removed lines in both themes.
 *
 * Validates that the --diff-* semantic tokens resolve to WCAG-AA-legible
 * colour pairs (≥ 4.5:1 contrast ratio) in both the dark and light
 * theme. The bug (2026-05-24) was light-green text on a light-green
 * background in the light shell because diff2html was always rendered
 * with dark-mode colors on a transparent wrapper.
 *
 * Approach: inject synthetic diff elements using the CSS custom
 * properties, then measure computed colour contrast programmatically.
 * This tests the TOKEN CONTRACT — the load-bearing invariant. If the
 * tokens regress, every diff surface (run-git-viewer, git-pane
 * diff2html, beautiful-results) breaks.
 */

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
  await page.waitForTimeout(80);
}

function parseRgb(value: string): [number, number, number, number] {
  const m = /rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(value);
  if (!m) throw new Error(`Cannot parse colour: ${value}`);
  return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] === undefined ? 1 : Number(m[4])];
}

function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map(c => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * Contrast ratio between two opaque RGB colours. If the foreground or
 * background has alpha < 1, it must be composited against the surface
 * BEFORE calling this.
 */
function contrastRatioOpaque(fg: [number, number, number], bg: [number, number, number]): number {
  const l1 = luminance(fg);
  const l2 = luminance(bg);
  const [light, dark] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (light + 0.05) / (dark + 0.05);
}

/**
 * Composite a colour (with alpha) onto an opaque surface.
 * Returns the effective opaque RGB.
 */
function composite(
  colour: [number, number, number, number],
  surface: [number, number, number]
): [number, number, number] {
  const a = colour[3];
  return [
    Math.round(colour[0] * a + surface[0] * (1 - a)),
    Math.round(colour[1] * a + surface[1] * (1 - a)),
    Math.round(colour[2] * a + surface[2] * (1 - a)),
  ];
}

/**
 * Compute WCAG contrast ratio between fg and bg, alpha-compositing
 * both against a given opaque surface (the underlying page background).
 */
function contrastOnSurface(fgRaw: string, bgRaw: string, surfaceRaw: string): number {
  const fg = parseRgb(fgRaw);
  const bg = parseRgb(bgRaw);
  const surface = parseRgb(surfaceRaw);
  const surfaceRgb: [number, number, number] = [surface[0], surface[1], surface[2]];

  const effectiveBg = composite(bg, surfaceRgb);
  const effectiveFg = composite(fg, effectiveBg);
  return contrastRatioOpaque(effectiveFg, effectiveBg);
}

test.describe('F53 — Diff-view contrast (added/removed lines)', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`--diff-* tokens WCAG-AA contrast (${theme})`, async ({ page }, testInfo) => {
      await page.goto('/');
      await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });
      await setTheme(page, theme);

      // Inject synthetic diff lines that consume --diff-* tokens the same
      // way run-git-viewer and the git-pane overrides do.
      await page.evaluate(() => {
        const host = document.createElement('div');
        host.id = 'f53-contrast-probe';
        host.style.cssText = 'position: fixed; inset: 40px; z-index: 99999; padding: 16px;'
          + 'background: var(--studio-bg-editor); font: 12px/1.55 ui-monospace, monospace;'
          + 'white-space: pre; overflow: auto;';
        host.innerHTML = [
          '<div data-probe="surface" style="background: var(--studio-bg-editor); color: var(--studio-fg); padding: 2px 4px;">context line</div>',
          '<div data-probe="add-text" style="background: var(--diff-add-bg); color: var(--diff-add-text); padding: 2px 4px;">+ added body text</div>',
          '<div data-probe="add-prefix" style="background: var(--diff-add-bg); color: var(--diff-add-fg); padding: 2px 4px;">+ prefix glyph</div>',
          '<div data-probe="add-gutter" style="background: var(--diff-add-gutter); color: var(--diff-add-fg); padding: 2px 4px;">  1</div>',
          '<div data-probe="rem-text" style="background: var(--diff-rem-bg); color: var(--diff-rem-text); padding: 2px 4px;">- removed body text</div>',
          '<div data-probe="rem-prefix" style="background: var(--diff-rem-bg); color: var(--diff-rem-fg); padding: 2px 4px;">- prefix glyph</div>',
          '<div data-probe="rem-gutter" style="background: var(--diff-rem-gutter); color: var(--diff-rem-fg); padding: 2px 4px;">  2</div>',
          '<div data-probe="hunk" style="background: var(--diff-hunk-bg); color: var(--diff-hunk-fg); padding: 2px 4px;">@@ -1,5 +1,6 @@</div>',
        ].join('');
        document.body.appendChild(host);
      });

      // Read the resolved surface colour (opaque parent).
      const surfaceColour = await page.evaluate(() => {
        const host = document.getElementById('f53-contrast-probe')!;
        return getComputedStyle(host).backgroundColor;
      });

      // Read all probe colours.
      const probes = await page.evaluate(() => {
        const host = document.getElementById('f53-contrast-probe')!;
        const out: Record<string, { color: string; bg: string }> = {};
        for (const el of host.querySelectorAll<HTMLElement>('[data-probe]')) {
          const cs = getComputedStyle(el);
          out[el.dataset['probe']!] = { color: cs.color, bg: cs.backgroundColor };
        }
        return out;
      });

      // Key pairs to assert (probe-name → minimum contrast).
      const assertions: [string, number][] = [
        ['add-text', 4.5],
        ['add-prefix', 4.5],
        ['rem-text', 4.5],
        ['rem-prefix', 4.5],
        ['hunk', 4.5],
      ];

      for (const [probe, minRatio] of assertions) {
        const { color, bg } = probes[probe];
        const ratio = contrastOnSurface(color, bg, surfaceColour);
        expect(
          ratio,
          `[${theme}] ${probe}: contrast ${ratio.toFixed(2)} (${color} on ${bg} over ${surfaceColour}) must be ≥ ${minRatio}`
        ).toBeGreaterThanOrEqual(minRatio);
      }

      await testInfo.attach(`f53-diff-tokens-${theme}.png`, {
        body: await page.screenshot({ fullPage: false }),
        contentType: 'image/png'
      });

      if (process.env.F53_RESULTS_DIR) {
        await page.screenshot({
          path: `${process.env.F53_RESULTS_DIR}/f53-diff-view-${theme}-after.png`,
          fullPage: false
        });
      }
    });
  }
});
