import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  extractWikiLinkedElements,
  findWikiAnchor,
  scrollToWikiAnchor,
  wikiAnchorId,
} from './wiki-linked-element';

describe('wiki linked elements', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('keeps the first eight explicit document anchors in stable order', () => {
    const links = extractWikiLinkedElements(Array.from({ length: 9 }, (_, index) =>
      `[Section ${index + 1}](#section-${index + 1})`).join('\n'));

    expect(links).toHaveLength(8);
    expect(links.map(link => link.target)).toEqual([
      '#section-1', '#section-2', '#section-3', '#section-4',
      '#section-5', '#section-6', '#section-7', '#section-8',
    ]);
  });

  it('finds and smoothly scrolls anchors inside an open ShadowRoot', () => {
    const root = document.createElement('section');
    const shadowHost = document.createElement('div');
    root.append(shadowHost);
    const shadow = shadowHost.attachShadow({ mode: 'open' });
    const target = document.createElement('h2');
    target.id = 'section one';
    const scrollIntoView = vi.fn();
    Object.defineProperty(target, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
    shadow.append(target);
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false })));

    expect(wikiAnchorId('#section%20one')).toBe('section one');
    expect(findWikiAnchor(root, '#section%20one')).toBe(target);
    expect(scrollToWikiAnchor(root, '#section%20one')).toBe(true);
    expect(scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'start' });
  });

  it('reports malformed and unresolved anchors instead of silently succeeding', () => {
    const root = document.createElement('section');

    expect(wikiAnchorId('#%not-encoded')).toBeNull();
    expect(scrollToWikiAnchor(root, '#missing')).toBe(false);
  });
});
