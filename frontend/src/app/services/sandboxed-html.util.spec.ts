import { describe, expect, it } from 'vitest';
import {
  ISOLATED_HTML_ACTIVE_ANCHOR_MESSAGE,
  ISOLATED_HTML_ANCHORS_READY_MESSAGE,
  ISOLATED_HTML_LINK_MESSAGE,
  ISOLATED_HTML_SCROLL_ANCHOR_MESSAGE,
  ISOLATED_HTML_TRACK_ANCHORS_MESSAGE,
  WORKBENCH_DECISION_CHANGE_MESSAGE,
  WORKBENCH_DECISION_HYDRATE_MESSAGE,
  buildIsolatedHtmlSrcdoc,
  resolveIsolatedHtmlNavigation,
} from './sandboxed-html.util';

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

  it('rewrites relative <img> sources via resolveAssetSrc and widens the CSP to the host origin', () => {
    const html = '<img src="assets/foo.png" alt="Foo"><img src="https://cdn.example/bar.png" alt="Bar">';
    const srcdoc = buildIsolatedHtmlSrcdoc(html, {
      resolveAssetSrc: (src) =>
        src.startsWith('assets/')
          ? `/api/projects/Demo/wiki/assets/operations/timeline-redesign/${src}`
          : src,
    });

    const expectedSrc = new URL(
      '/api/projects/Demo/wiki/assets/operations/timeline-redesign/assets/foo.png',
      window.location.origin,
    ).href;
    expect(srcdoc).toContain(`src="${expectedSrc}"`);
    expect(srcdoc).toContain('src="https://cdn.example/bar.png"');
    expect(srcdoc).toContain(`img-src data: ${window.location.origin};`);
  });

  it('leaves <img> sources and the CSP unchanged when no asset resolver is supplied', () => {
    const srcdoc = buildIsolatedHtmlSrcdoc('<img src="assets/foo.png" alt="Foo">');

    expect(srcdoc).toContain('src="assets/foo.png"');
    expect(srcdoc).toContain('img-src data:;');
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
