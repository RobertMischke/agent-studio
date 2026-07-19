import { toProjectSlug } from '../project-shell/project-shell.config';

/**
 * Shareable-URL contract for the project wiki rail.
 *
 * The wiki lives on the project-shell hash route `#/projects/<slug>/wiki`
 * (owned by ProjectOverlaysService). To make an open page or folder overview
 * bookmarkable we append a single query segment to that hash:
 *
 *   - open page          `#/projects/<slug>/wiki?page=<relPath>`
 *   - folder overview     `#/projects/<slug>/wiki?folder=<relPath>`
 *   - Overview / landing  `#/projects/<slug>/wiki`  (no param)
 *
 * `relPath` is percent-encoded (slashes included) so the value stays opaque and
 * never confuses the shell's `slug/rail` split. These are pure helpers so both
 * the component and its specs can build and parse the exact same strings.
 */
export type WikiDeepLinkTarget =
  | { kind: 'page'; relPath: string }
  | { kind: 'folder'; relPath: string }
  | { kind: 'overview' };

export { toProjectSlug };

/** The hash prefix for a project's wiki rail: `#/projects/<slug>/wiki`. */
export function wikiRouteHashBase(slug: string): string {
  return `#/projects/${slug}/wiki`;
}

/** Build the hash (with optional `?page` / `?folder` query) for a wiki target. */
export function buildWikiRouteHash(slug: string, target: WikiDeepLinkTarget): string {
  const base = wikiRouteHashBase(slug);
  if (target.kind === 'page') return `${base}?page=${encodeURIComponent(target.relPath)}`;
  if (target.kind === 'folder') return `${base}?folder=${encodeURIComponent(target.relPath)}`;
  return base;
}

/** True when `hash` addresses this project's wiki rail (with or without a param). */
export function isWikiRouteHash(hash: string, slug: string): boolean {
  const base = wikiRouteHashBase(slug);
  return hash === base || hash.startsWith(`${base}?`);
}

/**
 * Parse the `page` / `folder` deep-link out of a hash IF that hash is the wiki
 * rail route for `slug`. Returns `null` when the hash is not this wiki route, so
 * callers can leave the URL untouched when the wiki is mounted off its canonical
 * route (e.g. inside a studio Hub tab). A wiki route without a usable param
 * yields `{ kind: 'overview' }`.
 */
export function parseWikiRouteHash(hash: string, slug: string): WikiDeepLinkTarget | null {
  if (!isWikiRouteHash(hash, slug)) return null;
  const qIndex = hash.indexOf('?');
  if (qIndex < 0) return { kind: 'overview' };
  const params = new URLSearchParams(hash.slice(qIndex + 1));
  const page = params.get('page');
  if (page && page.trim()) return { kind: 'page', relPath: page };
  const folder = params.get('folder');
  if (folder && folder.trim()) return { kind: 'folder', relPath: folder };
  return { kind: 'overview' };
}

/** Absolute, shareable URL to a wiki target (used by the copy-link actions). */
export function buildWikiRouteUrl(
  location: { origin: string; pathname: string; search: string },
  slug: string,
  target: WikiDeepLinkTarget,
): string {
  return `${location.origin}${location.pathname}${location.search}${buildWikiRouteHash(slug, target)}`;
}
