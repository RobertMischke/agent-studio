/**
 * Detect repo source-file references inside protocol/result markdown so they
 * can render as clickable links that open the source viewer.
 *
 * The detector is intentionally conservative: it only ever runs against text
 * the author already wrapped in an inline `code` span (or used as a link
 * href), so it never touches free prose. Within that scope it still applies a
 * tight shape gate — no whitespace, a relative path, and a known source
 * extension — so shell snippets like `npm run build` or `git status` and
 * prose-y tokens like `e.g.` are rejected.
 */

export interface SourceRef {
  /** Repo-relative, forward-slash path (e.g. `backend/Services/Foo.cs`). */
  path: string;
  /** 1-based line to scroll to, or null when the reference has no line. */
  line: number | null;
}

// Source extensions worth linking. Kept broad enough to cover this repo
// (C# / TS / SCSS / HTML / JSON / YAML / Markdown / shell …) without being
// so loose that arbitrary `word.word` prose tokens slip through.
const SOURCE_EXTENSIONS = new Set([
  'ts', 'tsx', 'js', 'jsx', 'mjs', 'cjs',
  'cs', 'cshtml', 'razor',
  'scss', 'sass', 'css', 'less',
  'html', 'htm', 'xml', 'svg',
  'json', 'jsonc', 'yml', 'yaml', 'toml', 'ini', 'config', 'csproj', 'props', 'targets', 'sln',
  'md', 'mdx',
  'py', 'go', 'rs', 'java', 'kt', 'kts', 'rb', 'php', 'swift',
  'c', 'cc', 'cpp', 'cxx', 'h', 'hpp',
  'sh', 'bash', 'zsh', 'ps1', 'bat', 'cmd',
  'sql', 'graphql', 'gql', 'proto',
  'vue', 'svelte',
]);

// path: relative, forward/back slashes, dots, dashes, underscores. Must NOT
// start with a slash (no absolute paths) and must NOT contain `..` segments —
// the backend guards traversal anyway, but rejecting it here avoids matching
// ellipses ("...") and keeps the link semantics clean.
const PATH_SHAPE_RE = /^[A-Za-z0-9_.][A-Za-z0-9_./\\-]*$/;

// Trailing position suffix: `:line`, `:line:col`, or GitHub-style `#L<line>`.
const POSITION_RE = /(?::(\d+)(?::\d+)?|#L(\d+))$/i;

/**
 * Returns the source reference encoded by `raw`, or null when `raw` does not
 * look like a repo source path. Pure + synchronous so it is trivially unit-
 * testable and safe to call from the markdown renderer's hot path.
 */
export function detectSourceRef(raw: string | null | undefined): SourceRef | null {
  if (!raw) return null;
  let text = raw.trim();
  if (!text || /\s/.test(text)) return null;

  // Strip a leading `./` so `./src/foo.ts` is treated like `src/foo.ts`.
  text = text.replace(/^\.\//, '');

  let line: number | null = null;
  const pos = POSITION_RE.exec(text);
  if (pos) {
    line = Number.parseInt(pos[1] ?? pos[2], 10);
    if (!Number.isFinite(line) || line <= 0) line = null;
    text = text.slice(0, pos.index);
  }

  const path = text.replace(/\\/g, '/');
  if (!path || path.startsWith('/') || path.includes('..')) return null;
  if (!PATH_SHAPE_RE.test(text)) return null;

  const lastDot = path.lastIndexOf('.');
  const lastSlash = path.lastIndexOf('/');
  if (lastDot <= lastSlash + 1) return null; // no extension on the filename
  const ext = path.slice(lastDot + 1).toLowerCase();
  if (!SOURCE_EXTENSIONS.has(ext)) return null;

  return { path, line };
}
