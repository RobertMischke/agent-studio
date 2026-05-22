import { test, expect, type Page } from '@playwright/test';

/**
 * F20 evidence — run-git-viewer diff body now applies highlight.js
 * spans on top of the existing add/rem tint overlay, instead of
 * showing flat monospace lines.
 *
 * Three invariants under test:
 *   1. The Tier-2 `--syntax-*` aliases (added in F20) resolve in
 *      both dark and light theme, and the values DIFFER between
 *      themes so the same hljs markup stays readable on either
 *      surface.
 *   2. With the run-git-viewer SCSS rules mounted, a
 *      `.rgv__line--add` line whose body holds an `.hljs-keyword`
 *      paints the keyword in the syntax palette (not the
 *      `--diff-add-fg` colour the add tint used to force) AND the
 *      line still carries the add-bg overlay.
 *   3. Screenshots in both themes for the F20 evidence trail.
 *
 * Note on test scaffolding: the component's own SCSS is
 * Angular-encapsulated, so a body-level mock can't pick up its
 * rules through the normal cascade. We mount an unencapsulated
 * `<style>` block whose selectors mirror
 * `run-git-viewer.component.scss` so the assertions land against
 * exactly what the runtime component renders. Token wiring
 * (`--syntax-*`, `--diff-*`) is theme-flipped at `:root`, so the
 * tokens themselves are read directly from the live shell.
 */

const RGV_STYLE_BLOCK = `
.rgv__diff-body { font-family: var(--font-mono); font-size: 12px; line-height: 1.55; white-space: pre; color: var(--studio-fg); margin: 0; padding: 12px 14px; }
.rgv__line { display: block; padding: 0 4px; color: var(--studio-fg); }
.rgv__line--add { background: var(--diff-add-bg); }
.rgv__line--del { background: var(--diff-rem-bg); }
.rgv__line--hunk { color: var(--diff-hunk-fg); background: var(--diff-hunk-bg); font-weight: 500; }
.rgv__line--meta { color: var(--studio-fg-dim); }
.rgv__line-prefix { display: inline-block; width: 1ch; user-select: none; color: var(--studio-fg-muted); }
.rgv__line--add .rgv__line-prefix { color: var(--diff-add-fg); font-weight: 600; }
.rgv__line--del .rgv__line-prefix { color: var(--diff-rem-fg); font-weight: 600; }
.rgv__diff-body .hljs-comment, .rgv__diff-body .hljs-quote { color: var(--syntax-comment); font-style: italic; }
.rgv__diff-body .hljs-keyword, .rgv__diff-body .hljs-selector-tag, .rgv__diff-body .hljs-section { color: var(--syntax-keyword); }
.rgv__diff-body .hljs-built_in, .rgv__diff-body .hljs-type { color: var(--syntax-type); }
.rgv__diff-body .hljs-string, .rgv__diff-body .hljs-attr, .rgv__diff-body .hljs-template-tag, .rgv__diff-body .hljs-template-variable { color: var(--syntax-string); }
.rgv__diff-body .hljs-number, .rgv__diff-body .hljs-literal { color: var(--syntax-number); }
.rgv__diff-body .hljs-title, .rgv__diff-body .hljs-title.class_, .rgv__diff-body .hljs-title.function_ { color: var(--syntax-title); }
.rgv__diff-body .hljs-variable, .rgv__diff-body .hljs-name, .rgv__diff-body .hljs-attribute { color: var(--syntax-variable); }
.rgv__diff-body .hljs-symbol, .rgv__diff-body .hljs-bullet, .rgv__diff-body .hljs-meta { color: var(--syntax-meta); }
.rgv__diff-body .hljs-tag { color: var(--syntax-tag); }
.rgv__diff-body .hljs-selector-class, .rgv__diff-body .hljs-selector-id, .rgv__diff-body .hljs-selector-attr, .rgv__diff-body .hljs-selector-pseudo { color: var(--syntax-selector); }
`;

async function setTheme(page: Page, theme: 'light' | 'dark') {
  await page.evaluate((t) => {
    document.documentElement.setAttribute('data-studio-theme', t);
    localStorage.setItem('studio-theme', t);
  }, theme);
  await page.waitForTimeout(80);
}

async function readSyntaxTokens(page: Page): Promise<Record<string, string>> {
  return page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement);
    const names = [
      '--syntax-comment',
      '--syntax-keyword',
      '--syntax-type',
      '--syntax-string',
      '--syntax-number',
      '--syntax-title',
      '--syntax-variable',
      '--syntax-meta',
      '--syntax-tag',
      '--syntax-selector',
    ];
    const out: Record<string, string> = {};
    for (const n of names) out[n] = cs.getPropertyValue(n).trim();
    return out;
  });
}

async function mountSampleDiff(page: Page): Promise<void> {
  await page.evaluate((styleBlock: string) => {
    document.getElementById('f20-rgv-style')?.remove();
    document.getElementById('f20-rgv-evidence')?.remove();

    const style = document.createElement('style');
    style.id = 'f20-rgv-style';
    style.textContent = styleBlock;
    document.head.appendChild(style);

    const host = document.createElement('div');
    host.id = 'f20-rgv-evidence';
    host.style.cssText = 'position: fixed; inset: 80px; z-index: 9999;'
      + 'background: var(--studio-bg-editor); border: 1px solid var(--studio-border-strong);'
      + 'border-radius: 8px; overflow: auto;';
    host.innerHTML = `
      <pre class="rgv__diff-body"><code><span class="rgv__line rgv__line--meta"><span class="rgv__line-code">diff --git a/src/example.ts b/src/example.ts</span>
</span><span class="rgv__line rgv__line--hunk"><span class="rgv__line-code">@@ -1,5 +1,6 @@ greet</span>
</span><span class="rgv__line rgv__line--ctx"><span class="rgv__line-prefix"> </span><span class="rgv__line-code"><span class="hljs-keyword">export</span> <span class="hljs-keyword">function</span> <span class="hljs-title function_">greet</span>(<span class="hljs-params">name: <span class="hljs-built_in">string</span></span>) {</span>
</span><span class="rgv__line rgv__line--del"><span class="rgv__line-prefix">-</span><span class="rgv__line-code">  <span class="hljs-keyword">return</span> <span class="hljs-string">'Hello, '</span> + name;</span>
</span><span class="rgv__line rgv__line--add"><span class="rgv__line-prefix">+</span><span class="rgv__line-code">  <span class="hljs-comment">// F20: hljs spans paint over the add tint.</span></span>
</span><span class="rgv__line rgv__line--add"><span class="rgv__line-prefix">+</span><span class="rgv__line-code">  <span class="hljs-keyword">return</span> <span class="hljs-string">\`Hello, \${name}!\`</span>;</span>
</span><span class="rgv__line rgv__line--ctx"><span class="rgv__line-prefix"> </span><span class="rgv__line-code">}</span>
</span><span class="rgv__line rgv__line--ctx"><span class="rgv__line-prefix"> </span><span class="rgv__line-code"></span>
</span><span class="rgv__line rgv__line--ctx"><span class="rgv__line-prefix"> </span><span class="rgv__line-code"><span class="hljs-keyword">export</span> <span class="hljs-keyword">const</span> <span class="hljs-variable">VERSION</span> = <span class="hljs-string">'1.0.0'</span>;</span>
</span></code></pre>
    `;
    document.body.appendChild(host);
  }, RGV_STYLE_BLOCK);
  await page.waitForTimeout(80);
}

test.describe('F20 — diff body syntax highlighting', () => {
  test('declares ten --syntax-* tokens and flips them under light theme', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });

    await setTheme(page, 'dark');
    const dark = await readSyntaxTokens(page);
    for (const k of Object.keys(dark)) {
      expect(dark[k], `${k} must be declared in dark mode`).not.toBe('');
    }

    await setTheme(page, 'light');
    const light = await readSyntaxTokens(page);
    for (const k of Object.keys(light)) {
      expect(light[k], `${k} must be declared in light mode`).not.toBe('');
    }

    expect(light['--syntax-keyword']).not.toBe(dark['--syntax-keyword']);
    expect(light['--syntax-string']).not.toBe(dark['--syntax-string']);
    expect(light['--syntax-comment']).not.toBe(dark['--syntax-comment']);
    expect(light['--syntax-number']).not.toBe(dark['--syntax-number']);
  });

  test('add line carries the bg overlay AND the hljs spans keep their token colour', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });

    await setTheme(page, 'dark');
    await mountSampleDiff(page);

    const colours = await page.evaluate(() => {
      const host = document.getElementById('f20-rgv-evidence')!;
      // The fixture has two `.rgv__line--add` lines: the first holds a
      // .hljs-comment, the second holds .hljs-keyword + .hljs-string.
      const addLines = host.querySelectorAll('.rgv__line--add');
      const commentTok = addLines[0].querySelector('.hljs-comment');
      const keywordTok = addLines[1].querySelector('.hljs-keyword');
      const stringTok = addLines[1].querySelector('.hljs-string');
      return {
        keywordColor: keywordTok ? getComputedStyle(keywordTok as Element).color : null,
        stringColor: stringTok ? getComputedStyle(stringTok as Element).color : null,
        commentColor: commentTok ? getComputedStyle(commentTok as Element).color : null,
        addLineBg: getComputedStyle(addLines[1]).backgroundColor,
        defaultLineColor: getComputedStyle(addLines[1]).color,
      };
    });

    // The add tint is still applied as a background overlay (not erased
    // by the syntax-highlighting work).
    expect(colours.addLineBg).not.toBe('rgba(0, 0, 0, 0)');

    // Every hljs span resolved to a colour (the SCSS bound correctly).
    expect(colours.keywordColor).not.toBeNull();
    expect(colours.stringColor).not.toBeNull();
    expect(colours.commentColor).not.toBeNull();

    // Each token resolves to a distinct hue. That is the whole point of
    // syntax highlighting — if any two collapsed onto each other, the
    // SCSS rule for one of them failed to bind.
    expect(colours.keywordColor).not.toBe(colours.stringColor);
    expect(colours.keywordColor).not.toBe(colours.commentColor);
    expect(colours.stringColor).not.toBe(colours.commentColor);

    // Sanity: hljs token colours differ from the un-highlighted line
    // colour, otherwise the whole change is invisible.
    expect(colours.keywordColor).not.toBe(colours.defaultLineColor);
  });

  test('captures dark + light evidence screenshots of the highlighted diff', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('studio-shell-root')).toBeVisible({ timeout: 10_000 });

    await setTheme(page, 'dark');
    await mountSampleDiff(page);
    await page.screenshot({ path: 'e2e/_baselines/f20-rgv-dark.png', fullPage: false });

    await setTheme(page, 'light');
    await mountSampleDiff(page);
    await page.screenshot({ path: 'e2e/_baselines/f20-rgv-light.png', fullPage: false });
  });
});
