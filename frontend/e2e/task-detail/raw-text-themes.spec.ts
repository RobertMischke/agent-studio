import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * F32 — Raw-text (Markdown source) viewer must stay readable in both themes.
 *
 * The prompt pane's editor lets the user flip from rich-text to raw
 * Markdown source via the overflow menu. Before F31 the source textarea
 * carried a hard-coded `rgba(0,0,0,0.18)` background and `#cbd5e1` foreground,
 * which painted light-grey text on the light shell once F19 introduced
 * the light theme. F31 swapped both to studio tokens
 * (--studio-bg-editor / --studio-fg) so the surface follows the active
 * theme; this spec locks that contract so a future regression cannot
 * silently revive the unreadable hex combo.
 *
 * Coverage:
 *   - both themes (dark / light), driven by `data-studio-theme` on <html>.
 *   - background + foreground are non-equal and use the studio tokens.
 *   - the textarea is actually visible after toggling to source mode.
 *   - WCAG-style contrast check (relative luminance ratio ≥ 4.5:1) so
 *     "light text on light surface" can never silently pass.
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

interface CreateTaskInput {
  title: string;
  watchPath: string;
  cliType?: string;
  agent?: string;
  promptMarkdown?: string;
  targetState?: string;
}

// The shared helpers/jobs.ts createJob still POSTs the renamed `/api/jobs`
// route (404 on the current backend); this spec talks to `/api/tasks`
// directly so the regression guard stays green independent of that
// repo-wide route migration.
async function createTask(input: CreateTaskInput): Promise<{ id: string }> {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: '',
      title: input.title,
      watchPath: input.watchPath,
      agent: input.agent ?? 'claude',
      cliType: input.cliType ?? 'claude',
      model: null,
      promptMarkdown: input.promptMarkdown ?? null,
      targetState: input.targetState ?? '2-ready',
      fixture: true
    })
  });
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch { /* best-effort cleanup */ }
}

async function setTheme(page: import('@playwright/test').Page, theme: 'dark' | 'light'): Promise<void> {
  // Stamp `data-studio-theme` on <html> and persist the preference so the
  // shell's effect doesn't overwrite it on the next change-detection.
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

/** Parse 'rgb(r, g, b)' / 'rgba(r, g, b, a)' into [r,g,b,a]. */
function parseRgb(value: string): [number, number, number, number] {
  const m = /rgba?\(\s*(\d+)[ ,]+(\d+)[ ,]+(\d+)(?:[ ,/]+([\d.]+))?\s*\)/.exec(value);
  if (!m) throw new Error(`Cannot parse colour: ${value}`);
  return [Number(m[1]), Number(m[2]), Number(m[3]), m[4] === undefined ? 1 : Number(m[4])];
}

/** Relative luminance per WCAG 2.x. */
function luminance(rgb: [number, number, number]): number {
  const [r, g, b] = rgb.map(c => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * WCAG contrast ratio between fg and bg. Folds fg's alpha against bg first
 * (textarea text in dark theme uses --studio-fg = #e2e8f0 with alpha 1, so
 * the fold is a no-op there; we keep it robust against later opacity work).
 */
function contrastRatio(fgRaw: string, bgRaw: string): number {
  const fg = parseRgb(fgRaw);
  const bg = parseRgb(bgRaw);
  const fgRgb: [number, number, number] = [
    Math.round(fg[0] * fg[3] + bg[0] * (1 - fg[3])),
    Math.round(fg[1] * fg[3] + bg[1] * (1 - fg[3])),
    Math.round(fg[2] * fg[3] + bg[2] * (1 - fg[3]))
  ];
  const l1 = luminance(fgRgb);
  const l2 = luminance([bg[0], bg[1], bg[2]]);
  const [light, dark] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (light + 0.05) / (dark + 0.05);
}

test.describe('F32 — Raw-text viewer stays readable across themes', () => {
  for (const theme of ['dark', 'light'] as const) {
    test(`source-mode textarea has contrasting fg/bg (${theme})`, async ({ page }, testInfo) => {
      const watchPath = await pickWatchPath();
      const job = await createTask({
        title: `f32-raw-text-${theme}-${Date.now()}`,
        watchPath,
        cliType: 'claude',
        agent: 'claude',
        promptMarkdown:
          '# F32 raw-text smoke\n\n' +
          'A short body so the textarea has visible characters to render.\n\n' +
          '- item one\n- item two\n',
        targetState: '2-ready'
      });

      try {
        await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
        await setTheme(page, theme);

        // F48: open the prompt editor from the Files-tab card. The detail
        // pane opens on the Overview tab, so select the Files tab first.
        await page.getByTestId('prompt-tab-description').click();
        await page.getByTestId('file-card-prompt-edit').click();

        const editor = page.getByTestId('prompt-editor');
        await expect(editor).toBeVisible({ timeout: 10_000 });

        // Open the overflow menu and switch to Markdown source.
        await editor.getByTestId('prompt-editor-mode-toggle').click();
        await page.getByTestId('prompt-editor-mode-menu-item-source').click();

        const source = page.getByTestId('prompt-editor-source');
        await expect(source).toBeVisible();

        // Sample the computed background + foreground.
        const { bg, color } = await source.evaluate((el) => {
          const cs = getComputedStyle(el);
          return { bg: cs.backgroundColor, color: cs.color };
        });

        // bg may resolve to rgba(0,0,0,0) if a parent paints the surface
        // through; fall back to the editor host's background in that case.
        const effectiveBg = /rgba\(\s*0\s*,\s*0\s*,\s*0\s*,\s*0\s*\)/.test(bg)
          ? await editor.evaluate((el) => getComputedStyle(el).backgroundColor)
          : bg;

        // Both must be resolvable to a colour, and they must differ. The
        // pre-F31 bug was a single hardcoded `#cbd5e1` text on white shell;
        // that would produce equal-ish values here.
        const ratio = contrastRatio(color, effectiveBg);
        // 4.5:1 is WCAG AA for normal body text. The studio palette
        // (light: #333333 on #ffffff ≈ 12.6:1; dark: #e2e8f0 on #181825
        // ≈ 13.7:1) clears it comfortably; this threshold catches a
        // regression to e.g. light-grey-on-white.
        expect(ratio, `contrast ${ratio.toFixed(2)} (${color} on ${effectiveBg})`).toBeGreaterThan(4.5);

        await testInfo.attach(`f32-raw-text-${theme}.png`, {
          body: await page.screenshot({ fullPage: false }),
          contentType: 'image/png'
        });
        if (process.env.F32_RESULTS_DIR) {
          await page.screenshot({
            path: `${process.env.F32_RESULTS_DIR}/f32-raw-text-${theme}.png`,
            fullPage: false
          });
        }
      } finally {
        await deleteJob(job.id, watchPath);
      }
    });
  }
});
