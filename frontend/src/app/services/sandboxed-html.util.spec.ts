import { describe, expect, it } from 'vitest';
import {
  ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE,
  ISOLATED_HTML_ANCHORS_READY_MESSAGE,
  ISOLATED_HTML_CSP,
  ISOLATED_HTML_LINK_MESSAGE,
  ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE,
  ISOLATED_HTML_TRACK_ANCHORS_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_HYDRATE_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  resolveIsolatedHtmlNavigation,
} from './sandboxed-html.util';

function cspOf(srcdoc: string): string {
  return new DOMParser().parseFromString(srcdoc, 'text/html')
    .querySelector('meta[http-equiv="Content-Security-Policy"]')?.getAttribute('content') ?? '';
}

describe('sandboxed HTML navigation', () => {
  it('delegates non-anchor links to the host while keeping anchors in the frame', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc(
      '<a href="#local">Local</a><a href="../target/index.html">Target</a><h2 id="local">Local</h2>',
    );

    expect(srcdoc).toContain(`type: '${ISOLATED_HTML_LINK_MESSAGE}'`);
    expect(srcdoc).toContain("href.charAt(0) === '#'");
    expect(srcdoc).toContain('parent.postMessage');
    expect(srcdoc).toContain(ISOLATED_HTML_ANCHORS_READY_MESSAGE);
    expect(srcdoc).toContain(ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE);
    expect(srcdoc).toContain(ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE);
    expect(srcdoc).toContain(ISOLATED_HTML_TRACK_ANCHORS_MESSAGE);
    expect(srcdoc).toContain("reduceMotion ? 'auto' : 'smooth'");
  });

  it('adds the inert Dossier decision bridge only when the host requests it', () => {
    const html = '<p data-decision-id="route" data-decision-kind="single"><span data-option-id="direct">Direct</span></p>';
    const ordinary = buildIsolatedHtmlSrcdoc(html);
    const workbench = buildIsolatedHtmlSrcdoc(html, { workbenchDecisions: true });

    expect(ordinary).not.toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(workbench).toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(workbench).toContain(WORKBENCH_DECISION_HYDRATE_MESSAGE);
    expect(workbench).toContain('data-studio-decision-control');
  });

  it('uses the descriptor pattern as the authoritative article variant', () => {
    const ui = new DOMParser().parseFromString(
      buildIsolatedHtmlSrcdoc(
        '<html data-document-pattern="concept"><body></body></html>',
        { documentPattern: 'ui' },
      ),
      'text/html',
    );
    const fallback = new DOMParser().parseFromString(
      buildIsolatedHtmlSrcdoc('<html data-document-pattern="ui"><body></body></html>'),
      'text/html',
    );

    expect(ui.documentElement.dataset['documentPattern']).toBe('ui');
    expect(fallback.documentElement.dataset['documentPattern']).toBe('concept');
  });

  it('resolves docs-relative targets and rejects paths that escape docs', () => {
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      '../umsetzungsplan-zielbild/index.html',
    )).toEqual({
      kind: 'wiki',
      relPath: 'operations/umsetzungsplan-zielbild/index.html',
    });
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      '../../../secrets.txt',
    )).toBeNull();
    expect(resolveIsolatedHtmlNavigation(
      'outside-docs/index.html',
      './target.html',
    )).toBeNull();
  });

  it('keeps images restricted to data: URIs when no resolver is supplied (AGT-2665 default)', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc(
      '<img src="assets/task-timeline-agt-2577-current-light--real.png" alt="Screenshot">',
    );
    const parsed = new DOMParser().parseFromString(srcdoc, 'text/html');
    expect(parsed.querySelector('img')?.getAttribute('src'))
      .toBe('assets/task-timeline-agt-2577-current-light--real.png');
    expect(cspOf(srcdoc)).toBe(ISOLATED_HTML_CSP);
  });

  it('rewrites a Dossier-relative image src to the Wiki asset endpoint and widens img-src to that origin (AGT-2665)', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc(
      '<img src="assets/task-timeline-agt-2577-current-light--real.png" alt="Timeline, light theme">' +
      '<img src="https://tracker.invalid/pixel.png" alt="External">' +
      '<img src="data:image/svg+xml;base64,AAAA" alt="Inline">',
      {
        resolveImageSrc: src => src.startsWith('assets/')
          ? `/api/projects/Demo/wiki/assets/operations/timeline-redesign/${src}`
          : src,
      },
    );
    const parsed = new DOMParser().parseFromString(srcdoc, 'text/html');
    const [screenshot, external, inline] = Array.from(parsed.querySelectorAll('img'));
    expect(screenshot.getAttribute('src')).toBe(
      `${window.location.origin}/api/projects/Demo/wiki/assets/operations/timeline-redesign/` +
      'assets/task-timeline-agt-2577-current-light--real.png',
    );
    // Absolute/data: sources the resolver hands back unchanged stay untouched.
    expect(external.getAttribute('src')).toBe('https://tracker.invalid/pixel.png');
    expect(inline.getAttribute('src')).toBe('data:image/svg+xml;base64,AAAA');
    expect(cspOf(srcdoc)).toContain(`img-src data: ${window.location.origin};`);
    // Every other directive is unchanged, in particular connect-src still
    // denies the artifact any script-driven network access.
    expect(cspOf(srcdoc)).toContain("connect-src 'none'");
  });

  it('classifies absolute HTTP(S) links as external and rejects active schemes', () => {
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      'https://example.com/reference?q=1',
    )).toEqual({
      kind: 'external',
      url: 'https://example.com/reference?q=1',
    });
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      'javascript:alert(1)',
    )).toBeNull();
    expect(resolveIsolatedHtmlNavigation(
      'docs/operations/nordstern/index.html',
      'mailto:docs@example.com',
    )).toBeNull();
  });
});
