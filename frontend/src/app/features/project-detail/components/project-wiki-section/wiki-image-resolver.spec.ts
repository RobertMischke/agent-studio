import { describe, expect, it } from 'vitest';
import { resolveWikiImageSrc } from './wiki-image-resolver';

const toAssetUrl = (rel: string) => `/api/projects/Demo/wiki/assets/${rel}`;

describe('resolveWikiImageSrc', () => {
  it('resolves a sibling image against the doc folder', () => {
    expect(resolveWikiImageSrc('diagram.svg', 'concepts/overview.md', toAssetUrl))
      .toBe('/api/projects/Demo/wiki/assets/concepts/diagram.svg');
  });

  it('walks up parent folders with ..', () => {
    expect(resolveWikiImageSrc('../../images/detail.png', 'visual/features/x.md', toAssetUrl))
      .toBe('/api/projects/Demo/wiki/assets/images/detail.png');
  });

  it('resolves a path from a root-level doc', () => {
    expect(resolveWikiImageSrc('images/board.png', 'README.md', toAssetUrl))
      .toBe('/api/projects/Demo/wiki/assets/images/board.png');
  });

  it('strips a leading ./', () => {
    expect(resolveWikiImageSrc('./img/a.png', 'guide.md', toAssetUrl))
      .toBe('/api/projects/Demo/wiki/assets/img/a.png');
  });

  it('passes through absolute http(s) urls', () => {
    expect(resolveWikiImageSrc('https://x/y.png', 'a.md', toAssetUrl)).toBe('https://x/y.png');
  });

  it('passes through protocol-relative and data urls', () => {
    expect(resolveWikiImageSrc('//cdn/y.png', 'a.md', toAssetUrl)).toBe('//cdn/y.png');
    expect(resolveWikiImageSrc('data:image/png;base64,AAAA', 'a.md', toAssetUrl))
      .toBe('data:image/png;base64,AAAA');
  });

  it('passes through site-rooted paths unchanged', () => {
    expect(resolveWikiImageSrc('/assets/y.png', 'a.md', toAssetUrl)).toBe('/assets/y.png');
  });

  it('passes through references that escape the docs root', () => {
    expect(resolveWikiImageSrc('../../../secret.png', 'a.md', toAssetUrl)).toBe('../../../secret.png');
  });

  it('passes through empty src', () => {
    expect(resolveWikiImageSrc('', 'a.md', toAssetUrl)).toBe('');
  });
});
