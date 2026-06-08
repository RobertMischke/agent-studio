/**
 * Maps image references inside `status.md` to the API URLs that serve the
 * actual files from the job folder. Three cases are recognised:
 *
 *   `attachments/foo.png` → /api/tasks/{id}/attachments/foo.png
 *   `results/foo.png`     → /api/tasks/{id}/results/foo.png
 *   `results/a/foo.png`   → /api/tasks/{id}/screenshot?path=a%2Ffoo.png
 *   `foo.png`             → /api/tasks/{id}/results/foo.png  (legacy fallback)
 *
 * Anything else (`http(s)://…`, `data:`, unsafe traversals) is passed
 * through unchanged.
 *
 * Folder semantics live in docs/protocol-style.md — keep that in sync if you
 * add a new prefix here.
 */

const ATTACHMENTS_PREFIX = 'attachments/';
const RESULTS_PREFIX = 'results/';

export function resolveProtocolImageSrc(
  src: string,
  jobId: string | null | undefined,
  watchPath: string | null | undefined
): string {
  if (!src || !jobId) return src;

  if (/^(?:[a-z]+:)?\/\//i.test(src) || src.startsWith('data:')) return src;

  const watchQs = watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '';

  if (src.startsWith(ATTACHMENTS_PREFIX)) {
    const name = src.slice(ATTACHMENTS_PREFIX.length);
    if (!isPlainFileName(name)) return src;
    return `/api/tasks/${encodeURIComponent(jobId)}/attachments/${encodeURIComponent(name)}${watchQs}`;
  }

  if (src.startsWith(RESULTS_PREFIX)) {
    const name = src.slice(RESULTS_PREFIX.length);
    if (isPlainFileName(name)) {
      return `/api/tasks/${encodeURIComponent(jobId)}/results/${encodeURIComponent(name)}${watchQs}`;
    }
    if (!isSafeResultsPath(name)) return src;
    const qs = `?path=${encodeURIComponent(name)}${watchPath ? `&watchPath=${encodeURIComponent(watchPath)}` : ''}`;
    return `/api/tasks/${encodeURIComponent(jobId)}/screenshot${qs}`;
  }

  if (isPlainFileName(src)) {
    return `/api/tasks/${encodeURIComponent(jobId)}/results/${encodeURIComponent(src)}${watchQs}`;
  }

  return src;
}

function isPlainFileName(name: string): boolean {
  return name.length > 0 && !name.includes('/') && !name.includes('\\') && !name.includes('..');
}

function isSafeResultsPath(path: string): boolean {
  if (!path || path.includes('\\') || path.includes('..')) return false;
  const parts = path.split('/');
  return parts.length > 1 && parts.every(part => isPlainFileName(part));
}
