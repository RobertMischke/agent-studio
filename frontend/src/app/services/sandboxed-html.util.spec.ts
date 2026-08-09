import { describe, expect, it } from 'vitest';
import {
  ISOLATED_HTML_LINK_MESSAGE,
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
  });

  it('adds the inert Workbench decision bridge only when the host requests it', () => {
    const html = '<p data-decision-id="route" data-decision-kind="single"><span data-option-id="direct">Direct</span></p>';
    const ordinary = buildIsolatedHtmlSrcdoc(html);
    const workbench = buildIsolatedHtmlSrcdoc(html, { workbenchDecisions: true });

    expect(ordinary).not.toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(workbench).toContain(WORKBENCH_DECISION_CHANGE_MESSAGE);
    expect(workbench).toContain(WORKBENCH_DECISION_HYDRATE_MESSAGE);
    expect(workbench).toContain('data-studio-decision-control');
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
