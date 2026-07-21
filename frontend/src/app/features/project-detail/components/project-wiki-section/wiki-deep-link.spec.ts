import { describe, expect, it } from 'vitest';
import {
  buildWikiRouteHash,
  buildWikiRouteUrl,
  isWikiRouteHash,
  parseWikiRouteHash,
  wikiRouteHashBase,
} from './wiki-deep-link';

describe('wiki-deep-link', () => {
  const slug = 'demo';

  it('builds the bare rail hash for the overview target', () => {
    expect(wikiRouteHashBase(slug)).toBe('#/projects/demo/wiki');
    expect(buildWikiRouteHash(slug, { kind: 'overview' })).toBe('#/projects/demo/wiki');
  });

  it('encodes page and folder relPaths (slashes included) into the hash query', () => {
    expect(buildWikiRouteHash(slug, { kind: 'page', relPath: 'concepts/overview.md' }))
      .toBe('#/projects/demo/wiki?page=concepts%2Foverview.md');
    expect(buildWikiRouteHash(slug, { kind: 'folder', relPath: 'concepts/sub' }))
      .toBe('#/projects/demo/wiki?folder=concepts%2Fsub');
  });

  it('recognises the wiki rail hash with or without a param', () => {
    expect(isWikiRouteHash('#/projects/demo/wiki', slug)).toBe(true);
    expect(isWikiRouteHash('#/projects/demo/wiki?page=a.md', slug)).toBe(true);
    expect(isWikiRouteHash('#/projects/demo/overview', slug)).toBe(false);
    expect(isWikiRouteHash('#/projects/other/wiki', slug)).toBe(false);
    // Must not treat a different rail that merely starts with "wiki" as ours.
    expect(isWikiRouteHash('#/projects/demo/wiki-archive', slug)).toBe(false);
  });

  it('parses a page target back out of the hash, round-tripping the relPath', () => {
    const hash = buildWikiRouteHash(slug, { kind: 'page', relPath: 'concepts/overview.md' });
    expect(parseWikiRouteHash(hash, slug)).toEqual({ kind: 'page', relPath: 'concepts/overview.md' });
  });

  it('parses a folder target back out of the hash', () => {
    const hash = buildWikiRouteHash(slug, { kind: 'folder', relPath: 'concepts/sub' });
    expect(parseWikiRouteHash(hash, slug)).toEqual({ kind: 'folder', relPath: 'concepts/sub' });
  });

  it('returns the overview target for the bare rail (no param)', () => {
    expect(parseWikiRouteHash('#/projects/demo/wiki', slug)).toEqual({ kind: 'overview' });
    expect(parseWikiRouteHash('#/projects/demo/wiki?page=', slug)).toEqual({ kind: 'overview' });
  });

  it('returns null when the hash is not this wiki rail', () => {
    expect(parseWikiRouteHash('#/projects/other/wiki?page=a.md', slug)).toBeNull();
    expect(parseWikiRouteHash('', slug)).toBeNull();
  });

  it('recognises and parses the wiki rail inside a composite hash (coexisting filters=)', () => {
    // The wiki route is the hash's route segment; a board filters= segment
    // riding alongside must not hide it (url-hash.util.ts).
    const hash = '#/projects/demo/wiki?page=concepts%2Foverview.md&filters=type%3Abug';
    expect(isWikiRouteHash(hash, slug)).toBe(true);
    expect(parseWikiRouteHash(hash, slug)).toEqual({ kind: 'page', relPath: 'concepts/overview.md' });
  });

  it('builds an absolute shareable URL preserving origin, path, and search', () => {
    const url = buildWikiRouteUrl(
      { origin: 'https://studio.example', pathname: '/', search: '?flag=1' },
      slug,
      { kind: 'page', relPath: 'concepts/overview.md' },
    );
    expect(url).toBe('https://studio.example/?flag=1#/projects/demo/wiki?page=concepts%2Foverview.md');
  });
});
