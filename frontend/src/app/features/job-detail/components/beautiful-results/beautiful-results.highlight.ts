/**
 * Stage 2 of the beautiful-results pipeline: walk a rendered container,
 * find every code block decorated by `renderCodeBlock`, and swap its
 * (escaped) source for the highlight.js output. Idempotent — a node that
 * has already been highlighted is skipped via a `data-highlighted` flag.
 */
import { decodeSource } from './beautiful-results.renderer';
import { highlightBlock } from './highlight-lazy';

export async function applyHighlighting(container: HTMLElement | null): Promise<void> {
  if (!container) return;
  const nodes = container.querySelectorAll<HTMLElement>('[data-results-code]:not([data-highlighted])');
  if (nodes.length === 0) return;
  // Mark first so re-entrant calls (rapid signal updates) don't double-work.
  nodes.forEach((n) => n.setAttribute('data-highlighted', 'pending'));
  await Promise.all(
    Array.from(nodes).map(async (node) => {
      const source = decodeSource(node.getAttribute('data-source'));
      const lang = node.getAttribute('data-lang');
      const { html } = await highlightBlock(source, lang);
      // Container may have been replaced (different job, raw toggle) while
      // we awaited — only swap if still attached.
      if (node.isConnected) {
        node.innerHTML = html;
        node.setAttribute('data-highlighted', 'done');
      }
    })
  );
}
