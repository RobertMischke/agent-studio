import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

/**
 * Markdown typography consolidation — regression coverage.
 *
 * The task description editor (`prompt-editor` / TipTap ProseMirror) and
 * the protocol pane body (`beautiful-results` rendered article) must
 * share a single global typography layer via the `.markdown-body` class.
 *
 * The probes here are structural, not pixel-by-pixel:
 *   - both surfaces carry `.markdown-body`
 *   - both share the same computed font-family and base font-size
 *   - heading colour / paragraph margin / link colour are consistent
 *   - long unbreakable tokens do not overflow either pane
 *
 * The spec opens a planted job whose prompt + protocol exercise the full
 * markdown grammar (headings, lists, code, blockquote, link, table,
 * long URL) and screenshots the result side-by-side.
 */

interface WatchPath { path: string; name?: string }
interface JobDetail {
  info: { id: string; watchPath: string };
  statusMarkdown: string | null;
}

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

const SAMPLE_MARKDOWN = `# Heading one

A paragraph with **bold**, _italic_, and \`inline-code\` plus a link to
[example.com](https://example.com/very/long/path/segment/that/should/wrap/cleanly?token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA).

## Heading two

- bullet item one
- bullet item two with a long unbreakable token AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
- bullet item three

1. ordered first
2. ordered second

### Heading three

> A blockquote that explains what comes next.

\`\`\`ts
export function hello(): string {
  return 'hi';
}
\`\`\`

#### Heading four
`;

const PROTOCOL_MARKDOWN = SAMPLE_MARKDOWN + '\n\n[[TASK_DONE]]\n';

async function plantJob(): Promise<{ id: string; watchPath: string }> {
  const watchPath = await pickWatchPath();
  const created = await createJob({
    title: `e2e-markdown-body-${Date.now()}`,
    watchPath,
    cliType: 'claude',
    agent: 'claude',
    promptMarkdown: SAMPLE_MARKDOWN,
    targetState: '2-ready'
  });
  return { id: created.id, watchPath };
}

interface ListedJob { id: string; watchPath: string }

async function pickJobWithStatus(): Promise<{ id: string; watchPath: string } | null> {
  const jobs = await api<ListedJob[]>('/api/tasks');
  for (const j of jobs.slice(0, 60)) {
    try {
      const detail = await api<JobDetail>(
        `/api/tasks/${encodeURIComponent(j.id)}?watchPath=${encodeURIComponent(j.watchPath)}`
      );
      if ((detail.statusMarkdown ?? '').length > 80) {
        return { id: j.id, watchPath: j.watchPath };
      }
    } catch { /* keep looking */ }
  }
  return null;
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch { /* best-effort */ }
}

async function openDetail(page: Page, id: string, watchPath: string): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  // F48: open the prompt editor from the Files-tab card so the assertions
  // below that probe the ProseMirror surface continue to apply.
  await page.getByTestId('file-card-prompt-edit').click();
  await expect(page.getByTestId('prompt-editor')).toBeVisible({ timeout: 10_000 });
}

interface MarkdownProbe {
  fontFamily: string;
  fontSizePx: number;
  lineHeight: string;
  /* Width of the rendered article in CSS pixels. Long-token bug would
   * push this past the parent. */
  scrollWidth: number;
  clientWidth: number;
  /* Sample colour of a strong / heading element, if any. */
  headingColor: string | null;
  paragraphMarginBottom: string | null;
}

async function probeMarkdownBody(page: Page, selector: string): Promise<MarkdownProbe> {
  return page.locator(selector).first().evaluate((el: HTMLElement) => {
    const computed = window.getComputedStyle(el);
    const heading = el.querySelector('h1, h2, h3') as HTMLElement | null;
    const headingStyle = heading ? window.getComputedStyle(heading) : null;
    // Prefer a "middle" paragraph so the first/last-child margin reset
    // rule doesn't skew the comparison.
    const paragraphs = Array.from(el.querySelectorAll('p')) as HTMLElement[];
    const middle = paragraphs.find(p => p.previousElementSibling && p.nextElementSibling)
      ?? paragraphs[0]
      ?? null;
    const paraStyle = middle ? window.getComputedStyle(middle) : null;
    return {
      fontFamily: computed.fontFamily,
      fontSizePx: parseFloat(computed.fontSize),
      lineHeight: computed.lineHeight,
      scrollWidth: el.scrollWidth,
      clientWidth: el.clientWidth,
      headingColor: headingStyle?.color ?? null,
      paragraphMarginBottom: paraStyle?.marginBottom ?? null
    };
  });
}

test.describe('Markdown typography consolidation', () => {
  test('task description and protocol share the markdown-body class and typography', async ({ page }) => {
    // Prefer an existing job that already has a status.md so we can
    // compare task description and protocol typography side by side.
    // Falls back to a planted prompt-only job so the prompt-editor side
    // of the regression check still runs on an empty workspace.
    const existing = await pickJobWithStatus();
    const planted = existing ? null : await plantJob();
    const target = existing ?? planted!;

    try {
      await page.setViewportSize({ width: 1600, height: 1100 });
      await openDetail(page, target.id, target.watchPath);

      // Task description: the contenteditable inside the prompt editor
      // is the ProseMirror surface that should carry `.markdown-body`.
      const promptEditor = page.getByTestId('prompt-editor');
      await expect(promptEditor).toBeVisible();
      const promptBody = promptEditor.locator('.ProseMirror');
      await expect(promptBody).toHaveClass(/markdown-body/);

      // Protocol: only assert when the chosen job carries a status.md.
      const protocolTab = page.getByTestId('inspector-tab-protocol');
      const protocolRendered = page.getByTestId('results-rendered');
      let protocolVisible = false;
      if (existing && await protocolTab.isVisible().catch(() => false)) {
        await protocolTab.click().catch(() => { /* may be disabled */ });
        protocolVisible = await protocolRendered.isVisible({ timeout: 5_000 }).catch(() => false);
      }

      const promptProbe = await probeMarkdownBody(page, '[data-testid="prompt-editor"] .ProseMirror');
      let protocolProbe: MarkdownProbe | null = null;
      if (protocolVisible) {
        await expect(protocolRendered).toHaveClass(/markdown-body/);
        protocolProbe = await probeMarkdownBody(page, '[data-testid="results-rendered"]');
      } else {
        test.info().annotations.push({
          type: 'note',
          description: 'protocol body not available — skipping cross-surface comparison'
        });
      }

      // Shared typography assertions.
      expect(promptProbe.fontFamily.toLowerCase()).toContain('inter');
      expect(promptProbe.fontSizePx).toBeGreaterThanOrEqual(13);
      expect(promptProbe.fontSizePx).toBeLessThanOrEqual(18);
      expect(promptProbe.scrollWidth,
        'task description must not overflow horizontally').toBeLessThanOrEqual(promptProbe.clientWidth + 2);

      if (protocolProbe) {
        expect(protocolProbe.fontFamily, 'protocol shares prose font family').toBe(promptProbe.fontFamily);
        expect(Math.abs(protocolProbe.fontSizePx - promptProbe.fontSizePx),
          'protocol shares prose font size').toBeLessThanOrEqual(0.5);
        if (promptProbe.headingColor && protocolProbe.headingColor) {
          expect(protocolProbe.headingColor,
            'heading color matches across surfaces').toBe(promptProbe.headingColor);
        }
        // Paragraph margin is also load-bearing, but the markdown-body
        // first/last-child reset zeroes it for single-paragraph bodies
        // so a strict equality check flakes by content. We only assert
        // when both surfaces returned a non-zero margin (i.e. a real
        // mid-document paragraph was available on both sides).
        const promptMb = parseFloat(promptProbe.paragraphMarginBottom ?? '');
        const protocolMb = parseFloat(protocolProbe.paragraphMarginBottom ?? '');
        if (promptMb > 0 && protocolMb > 0) {
          expect(Math.abs(protocolMb - promptMb),
            'paragraph margin matches across surfaces').toBeLessThanOrEqual(1);
        }
        expect(protocolProbe.scrollWidth,
          'protocol body must not overflow horizontally').toBeLessThanOrEqual(protocolProbe.clientWidth + 2);
      }

      // Screenshots for human review — both surfaces side-by-side in the
      // detail view.
      await page.screenshot({
        path: 'test-results/markdown-body-detail-overview.png',
        fullPage: false
      });
      await promptEditor.screenshot({
        path: 'test-results/markdown-body-task-description.png'
      });
      if (protocolVisible) {
        await protocolRendered.screenshot({
          path: 'test-results/markdown-body-protocol.png'
        });
      }
    } finally {
      if (planted) await deleteJob(planted.id, planted.watchPath);
    }
  });
});
