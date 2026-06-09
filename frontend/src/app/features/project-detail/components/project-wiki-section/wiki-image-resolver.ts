/**
 * Resolves a relative image/diagram reference inside a wiki doc to the
 * backend asset URL so it renders in place. Wiki docs use paths relative to
 * the document's own folder (e.g. `../../images/foo.png` from
 * `visual/features/x.md`), so the reference is first normalised against the
 * opened doc's directory and then mapped to `/wiki/assets/<relPath>`.
 *
 * Absolute URLs (`http(s)://`, protocol-relative `//`, `data:`), site-rooted
 * paths (`/foo.png`), and references that escape the docs root are passed
 * through unchanged.
 */
export function resolveWikiImageSrc(
  src: string,
  currentDocRelPath: string,
  toAssetUrl: (assetRelPath: string) => string,
): string {
  if (!src) return src;
  if (/^(?:[a-z]+:)?\/\//i.test(src) || src.startsWith('data:')) return src;
  if (src.startsWith('/')) return src;

  const normalised = normaliseAssetPath(currentDocRelPath, src);
  if (normalised == null) return src;
  return toAssetUrl(normalised);
}

/**
 * Joins the directory of `docRelPath` with `src` and collapses `.`/`..`
 * segments. Returns null when the result climbs above the docs root (a
 * reference we cannot serve through the assets endpoint).
 */
function normaliseAssetPath(docRelPath: string, src: string): string | null {
  const docDir = docRelPath.split('/').slice(0, -1);
  const segments = [...docDir, ...src.split('/')];
  const stack: string[] = [];
  for (const seg of segments) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') {
      if (stack.length === 0) return null;
      stack.pop();
      continue;
    }
    stack.push(seg);
  }
  return stack.length === 0 ? null : stack.join('/');
}
