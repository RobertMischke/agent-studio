export interface TaskArtifactLinkContext {
  jobId: string | null | undefined;
  watchPath: string | null | undefined;
}

export interface ResolvedTaskArtifactLink {
  /** Task-folder-relative path shown to operators and used by tests. */
  relativePath: string;
  /** Guarded API URL bound to the currently open task. */
  href: string;
  /** HTML artifacts render inline; other types keep their response-type behavior. */
  html: boolean;
}

type ArtifactFolder = 'results' | 'logs';

/**
 * Resolve links emitted by an agent back into the currently open task folder.
 *
 * Agents normally emit `results/report.html` or `logs/cli-output.log`, but CLI
 * completion messages sometimes contain the absolute runner-side
 * `JOB_RESULTS_DIR`. The absolute prefix is execution-host state and must never
 * become browser navigation state, so only the `results/` or `logs/` suffix is
 * retained and rebound to the card context.
 */
export function resolveTaskArtifactLink(
  rawHref: string | null | undefined,
  context: TaskArtifactLinkContext,
): ResolvedTaskArtifactLink | null {
  const jobId = context.jobId?.trim();
  if (!jobId || !rawHref?.trim()) return null;

  const parsed = extractArtifactPath(rawHref);
  if (!parsed) return null;
  if (parsed.folder === 'logs' && !isAllowedLog(parsed.segments.at(-1) ?? '')) return null;

  const encodedPath = parsed.segments.map(encodeURIComponent).join('/');
  const task = encodeURIComponent(jobId);
  const watchPath = context.watchPath?.trim();
  const watchQuery = watchPath ? `watchPath=${encodeURIComponent(watchPath)}` : '';
  const fragment = parsed.fragment ? `#${encodeURIComponent(parsed.fragment)}` : '';

  if (parsed.folder === 'results') {
    const query = watchQuery ? `?${watchQuery}` : '';
    return {
      relativePath: `results/${parsed.segments.join('/')}`,
      href: `/api/tasks/${task}/results/${encodedPath}${query}${fragment}`,
      html: isHtml(parsed.segments.at(-1) ?? ''),
    };
  }

  const query = [watchQuery, 'scope=workspace'].filter(Boolean).join('&');
  return {
    relativePath: `logs/${parsed.segments.join('/')}`,
    href: `/api/tasks/${task}/files/logs/${encodedPath}?${query}${fragment}`,
    html: isHtml(parsed.segments.at(-1) ?? ''),
  };
}

function extractArtifactPath(rawHref: string): {
  folder: ArtifactFolder;
  segments: string[];
  fragment: string;
} | null {
  const trimmed = rawHref.trim();
  if (
    /^(?:https?:|mailto:|data:|javascript:|blob:)/i.test(trimmed)
    || trimmed.startsWith('#')
    || /^\/api\//i.test(trimmed)
  ) {
    return null;
  }

  const hashAt = trimmed.indexOf('#');
  const fragment = hashAt >= 0 ? safeDecode(trimmed.slice(hashAt + 1)) : '';
  const withoutHash = hashAt >= 0 ? trimmed.slice(0, hashAt) : trimmed;
  const queryAt = withoutHash.indexOf('?');
  let path = (queryAt >= 0 ? withoutHash.slice(0, queryAt) : withoutHash)
    .replace(/\\/g, '/');

  // `file://` is execution-host state too. The renderer may strip this scheme,
  // but accepting it here keeps the pure resolver safe for every host path.
  path = path.replace(/^file:\/\//i, '');
  path = path.replace(/^\.\//, '');

  let folder: ArtifactFolder | null = null;
  let rest = '';
  const relative = /^(results|logs)\/(.+)$/i.exec(path);
  if (relative) {
    folder = relative[1].toLowerCase() as ArtifactFolder;
    rest = relative[2];
  } else if (path.startsWith('/') || /^[A-Za-z]:\//.test(path)) {
    const absolute = /\/(results|logs)\/(.+)$/i.exec(path);
    if (!absolute) return null;
    folder = absolute[1].toLowerCase() as ArtifactFolder;
    rest = absolute[2];
  }
  if (!folder || !rest) return null;

  const segments: string[] = [];
  for (const rawSegment of rest.split('/')) {
    const segment = safeDecode(rawSegment);
    if (
      !segment
      || segment === '.'
      || segment === '..'
      || /[\\/]/.test(segment)
      || hasControlCharacter(segment)
    ) return null;
    segments.push(segment);
  }
  if (segments.length === 0) return null;
  return { folder, segments, fragment };
}

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

function isHtml(fileName: string): boolean {
  return /\.html?$/i.test(fileName);
}

function isAllowedLog(fileName: string): boolean {
  return /\.(?:log|txt|md|json|jsonl|ndjson|csv|xml|ya?ml)$/i.test(fileName);
}

function hasControlCharacter(value: string): boolean {
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (code <= 31 || code === 127) return true;
  }
  return false;
}
