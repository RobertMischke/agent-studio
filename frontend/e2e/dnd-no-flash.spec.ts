/**
 * Drag-and-drop polish: motion contract.
 *
 * Closes the "brightness flash on drop" regression by pinning three rules
 * from docs/design-principles.md "Motion":
 *
 *   1. The dropped card's transition list does NOT contain `background` or
 *      `filter`. Only `opacity`, `transform`, and `box-shadow` may animate
 *      during drag-and-drop. The original bug: an indigo drop-zone glow
 *      with a `background-color` transition + wide `box-shadow` lingered
 *      ~80ms under the just-landed card and read as a brightness flash.
 *   2. The drop-zone bar fades in/out via `opacity` only - no background
 *      transition and no shadow glow that leaks past its layout bounds.
 *   3. Under `prefers-reduced-motion: reduce`, the card's transition and
 *      the drop-zone's transition both collapse to zero duration.
 *
 * Test shape (matches the project's canonical pattern for specs that pin
 * a CSS contract without depending on a live frontend - see
 * `live-decision-banner.spec.ts` and `orchestrator-review-subsection.spec.ts`):
 * both tests load the production SCSS off disk into an inline `<style>`
 * tag and walk the CSSOM. They run against any chromium - no backend, no
 * frontend, no fixture - so they survive the dev-offline policy.
 *
 * The "drop -> optimistic DOM reorder within one animation frame"
 * assertion called for by the original brief is already covered by
 * `lane-reorder-drag.spec.ts`, which stalls the reorder POST and asserts
 * the new DOM order appears before the POST resolves. That spec is the
 * single source of truth for the optimistic-paint contract; duplicating
 * it here would have meant two flaky route-timing tests for the same
 * behaviour, with no extra regression coverage for the flash.
 */
import { test as plainTest, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

// Resolve the production SCSS files. The CSS rules we lock here are
// plain CSS (no SCSS-specific syntax in the motion section), so reading
// them as text and inlining as <style> is sufficient. The :host pseudo
// only matters for shadow-DOM encapsulation, which we don't need for the
// rule-list assertions.
const REPO_ROOT = path.resolve(__dirname, '..', '..');
const CARD_SCSS_PATH = path.join(
  REPO_ROOT,
  'frontend/src/app/features/board/components/job-card/job-card.component.scss'
);
const COLUMN_SCSS_PATH = path.join(
  REPO_ROOT,
  'frontend/src/app/features/board/components/job-column.scss'
);

function loadScss(p: string): string {
  return fs.readFileSync(p, 'utf8');
}

// The production SCSS uses :host(.drag-source) .job-card; for a plain
// HTML harness we mirror that with .drag-source .job-card so the same
// rule list is computable on the test card. Other selectors are bare CSS.
function adaptForHarness(scss: string): string {
  return scss.replace(/:host\((\.[a-zA-Z0-9_-]+)\)\s+/g, '$1 ');
}

const HARNESS_HTML = `<!doctype html>
<html><head><meta charset="utf-8"><title>dnd motion contract</title>
<style id="card-css">${adaptForHarness(loadScss(CARD_SCSS_PATH))}</style>
<style id="column-css">${loadScss(COLUMN_SCSS_PATH)}</style>
</head>
<body style="margin:0;padding:24px;background:#181825;font-family:-apple-system,sans-serif;">
  <div class="column" data-testid="column">
    <div class="column__header"><h2 class="column__title">Ready</h2></div>
    <div class="column__body">
      <div class="column__drop-zone column__drop-zone--active" data-testid="dropzone"></div>
      <div class="job-card job-card--2-ready" data-testid="card">
        <div class="job-card__title">test card</div>
      </div>
      <div class="column__drop-zone" data-testid="dropzone-idle"></div>
    </div>
  </div>
</body></html>`;

plainTest.describe('Drag-and-drop motion CSS contract (static harness)', () => {
  plainTest('card transition list contains no background or filter; drop-zone fades via opacity', async ({ page }) => {
    await page.setContent(HARNESS_HTML, { waitUntil: 'load' });
    const card = page.getByTestId('card');
    await expect(card).toBeVisible();
    // Visual evidence: the active drop-zone bar fades in via opacity only,
    // no glow leaks past its 4px-inset bounds (the pre-fix shadow was the
    // source of the bright-then-dim flash under the landed card).
    await page.locator('.column').screenshot({
      path: 'test-results/dnd-no-flash-harness.png'
    });

    const cardTransition = await card.evaluate(el => getComputedStyle(el as HTMLElement).transitionProperty);

    // Load-bearing rule for the flash regression: neither `background`
    // nor `filter` may participate in the card's transition list. They
    // were never explicitly listed pre-fix; the original flash came from
    // the column__drop-zone::before glow underneath the card, but the
    // card's own transition list is still the cleanest spot to pin the
    // motion rule: a future refactor that adds `transition: all` or
    // `background-color 0.x ease` to .job-card would re-introduce the
    // exact symptom.
    expect(cardTransition, `card transitionProperty: ${cardTransition}`).not.toContain('background');
    expect(cardTransition, `card transitionProperty: ${cardTransition}`).not.toContain('filter');
    // Positive: transform must animate (handles post-drop settle).
    expect(cardTransition).toContain('transform');

    // For the drop-zone ::before we cannot use getComputedStyle (pseudo
    // elements are inspected by reading the parsed stylesheet rule). We
    // walk document.styleSheets the same way the live page would.
    const zoneTransition = await page.evaluate(() => {
      const sheets = Array.from(document.styleSheets) as CSSStyleSheet[];
      // Look only at top-level CSSStyleRules so we don't accidentally pick
      // up the @media (prefers-reduced-motion) override that also targets
      // .column__drop-zone::before with `transition: none`.
      for (const sheet of sheets) {
        let rules: CSSRuleList;
        try { rules = sheet.cssRules; } catch { continue; }
        for (const r of Array.from(rules)) {
          if (!(r instanceof CSSStyleRule)) continue;
          if (r.selectorText !== '.column__drop-zone::before') continue;
          const s = r.style.transition || r.cssText;
          if (s) return s.trim();
        }
      }
      return '';
    });

    // The drop-zone bar may ONLY transition opacity. Background and
    // box-shadow are deliberately not in the list - the pre-fix code had
    // `transition: background 0.08s ease, height 0.08s ease;` with a
    // wide `box-shadow` glow that produced the flash.
    expect(zoneTransition, `drop-zone ::before transition: "${zoneTransition}"`).toContain('opacity');
    expect(zoneTransition).not.toContain('background');
    expect(zoneTransition).not.toContain('filter');
    expect(zoneTransition).not.toContain('box-shadow');
  });

  plainTest('prefers-reduced-motion: reduce collapses card and drop-zone transitions to zero duration', async ({ browser }) => {
    const context = await browser.newContext({ reducedMotion: 'reduce' });
    const page = await context.newPage();
    try {
      await page.setContent(HARNESS_HTML, { waitUntil: 'load' });
      const card = page.getByTestId('card');
      await expect(card).toBeVisible();

      const cardDuration = await card.evaluate(el => getComputedStyle(el as HTMLElement).transitionDuration);
      const allZero = cardDuration
        .split(',')
        .map(s => s.trim())
        .every(s => s === '0s' || s === '0ms');
      expect(
        allZero,
        `card transitionDuration under reduced-motion should be all 0s; got "${cardDuration}"`
      ).toBeTruthy();

      const dropzoneRuleFound = await page.evaluate(() => {
        const sheets = Array.from(document.styleSheets) as CSSStyleSheet[];
        for (const sheet of sheets) {
          let rules: CSSRuleList;
          try { rules = sheet.cssRules; } catch { continue; }
          for (const r of Array.from(rules)) {
            if (!(r instanceof CSSMediaRule)) continue;
            if (!r.conditionText.includes('prefers-reduced-motion')) continue;
            for (const inner of Array.from(r.cssRules)) {
              if (!(inner instanceof CSSStyleRule)) continue;
              if (inner.selectorText === '.column__drop-zone::before' &&
                  (inner.style.transition === 'none' || inner.cssText.includes('transition: none'))) {
                return true;
              }
            }
          }
        }
        return false;
      });
      expect(
        dropzoneRuleFound,
        'a @media (prefers-reduced-motion: reduce) block must disable the drop-zone transition'
      ).toBeTruthy();
    } finally {
      await context.close();
    }
  });
});
