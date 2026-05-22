import { test, expect, type Page } from '@playwright/test';

/**
 * F18 evidence — light-theme readability for diff renderings.
 *
 * Validates the change introduced 2026-05-22:
 *   • the studio-shell `:root` block declares `--diff-add-bg/-fg`,
 *     `--diff-rem-bg/-fg`, `--diff-hunk-bg/-fg` (six diff tokens).
 *   • `[data-studio-theme='light']` overrides them with WCAG-AA-safe
 *     pastel-on-dark-text values (#dcfce7 + #14532d for add, etc).
 *   • the run-git-viewer + diff-tab-view consume the tokens, so the
 *     surfaces are reachable from one swap, not 30 component-local
 *     hexes.
 *
 * The test only verifies the *token wiring*; the actual modal/tab
 * surfaces require a job with commits to render, which depends on
 * fixture data. Token wiring is the load-bearing invariant: if it
 * regresses, every diff surface breaks.
 */

async function setTheme(page: Page, theme: 'light' | 'dark') {
  await page.evaluate((t) => {
    document.documentElement.setAttribute('data-studio-theme', t);
    localStorage.setItem('studio-theme', t);
  }, theme);
}

async function readDiffTokens(page: Page): Promise<Record<string, string>> {
  return page.evaluate(() => {
    const root = document.documentElement;
    const cs = getComputedStyle(root);
    const names = [
      '--diff-add-bg',
      '--diff-add-fg',
      '--diff-rem-bg',
      '--diff-rem-fg',
      '--diff-hunk-bg',
      '--diff-hunk-fg',
    ];
    const out: Record<string, string> = {};
    for (const n of names) out[n] = cs.getPropertyValue(n).trim();
    return out;
  });
}

test.describe('F18 — diff token wiring', () => {
  test('declares six --diff-* tokens in :root and flips them under light theme', async ({ page }) => {
    await page.goto('/');
    // Wait for studio-shell SCSS to attach so the `:root` declarations
    // are present (the tokens are declared inside the studio-shell
    // component's component-host stylesheet).
    await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });

    // Dark theme
    await setTheme(page, 'dark');
    const dark = await readDiffTokens(page);
    for (const k of Object.keys(dark)) {
      expect(dark[k], `${k} must be declared in dark mode`).not.toBe('');
    }

    // Light theme
    await setTheme(page, 'light');
    const light = await readDiffTokens(page);
    for (const k of Object.keys(light)) {
      expect(light[k], `${k} must be declared in light mode`).not.toBe('');
    }

    // Light overrides must differ from dark for at least add-bg/rem-bg,
    // which is the failure mode operators reported (pastel on pastel
    // in light because the dark rgba() literal never flipped).
    expect(light['--diff-add-bg']).not.toBe(dark['--diff-add-bg']);
    expect(light['--diff-rem-bg']).not.toBe(dark['--diff-rem-bg']);
    expect(light['--diff-add-fg']).not.toBe(dark['--diff-add-fg']);
    expect(light['--diff-rem-fg']).not.toBe(dark['--diff-rem-fg']);
  });

  test('captures studio shell in light + dark for the F18 evidence trail', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });

    await setTheme(page, 'light');
    await page.waitForTimeout(120);
    await page.screenshot({ path: 'e2e/_baselines/f18-studio-light.png', fullPage: false });

    await setTheme(page, 'dark');
    await page.waitForTimeout(120);
    await page.screenshot({ path: 'e2e/_baselines/f18-studio-dark.png', fullPage: false });
  });

  test('renders a sample diff using the --diff-* tokens in both themes', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });

    // Inject a tiny diff renderer next to the studio shell so we can
    // visually assert the token colours land as expected. Mirrors the
    // markup in run-git-viewer.component.html (.rgv__line--add/del/hunk).
    await page.evaluate(() => {
      const host = document.createElement('div');
      host.id = 'f18-diff-evidence';
      host.style.cssText = 'position: fixed; inset: 80px; z-index: 9999; padding: 16px;'
        + 'background: var(--studio-bg-editor); border: 1px solid var(--studio-border-strong);'
        + 'border-radius: 8px; font: 12px/1.55 ui-monospace, monospace;'
        + 'color: var(--studio-fg); white-space: pre; overflow: auto;';
      host.innerHTML = [
        '<div style="background: var(--diff-hunk-bg); color: var(--diff-hunk-fg); padding: 0 4px;">@@ -1,5 +1,6 @@ greet</div>',
        '<div style="color: var(--studio-fg); padding: 0 4px;"> export function greet(name: string) {</div>',
        '<div style="background: var(--diff-rem-bg); color: var(--diff-rem-fg); padding: 0 4px;">-  return \'Hello, \' + name;</div>',
        '<div style="background: var(--diff-add-bg); color: var(--diff-add-fg); padding: 0 4px;">+  // diff2html renders this with proper highlighting.</div>',
        '<div style="background: var(--diff-add-bg); color: var(--diff-add-fg); padding: 0 4px;">+  return `Hello, ${name}!`;</div>',
        '<div style="color: var(--studio-fg); padding: 0 4px;"> }</div>',
      ].join('');
      document.body.appendChild(host);
    });

    await setTheme(page, 'light');
    await page.waitForTimeout(120);
    await page.screenshot({ path: 'e2e/_baselines/f18-diff-light.png', fullPage: false });

    await setTheme(page, 'dark');
    await page.waitForTimeout(120);
    await page.screenshot({ path: 'e2e/_baselines/f18-diff-dark.png', fullPage: false });
  });
});
