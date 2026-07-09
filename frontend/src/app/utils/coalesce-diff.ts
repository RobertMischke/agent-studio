/**
 * Coalesce a unified-diff blob so every change to the *same* file renders
 * under a single file header, with its hunks grouped below it.
 *
 * Why: the aggregated task-commit diff (`GET /{jobId}/commits/diff`)
 * concatenates the per-commit `git show` output. A file touched in more
 * than one attributed commit therefore appears as several independent
 * `diff --git a/README.md b/README.md` sections. diff2html renders one
 * `.d2h-file-wrapper` (with its own "README" header) per section, so the
 * same file looks like it changed several separate times — the exact
 * "Datei-Gruppierung" display bug reported at AGT-1920 (two README.md).
 *
 * The fix normalises the text before it reaches diff2html: sections that
 * target the same file are merged into one, keeping the first section's
 * header and appending every section's hunks in original order (each hunk
 * still carries its own `@@ ... @@` separator, so the individual changes
 * stay distinguishable). Genuinely different files keep their own headers,
 * so a real multi-file diff is untouched.
 *
 * The transform is a no-op when no file path repeats, so single-file and
 * already-grouped diffs pass through byte-for-byte.
 */

interface DiffSection {
  /** Stable identity for grouping: the b-side path (falls back to a-side). */
  readonly key: string;
  /** Header lines: `diff --git` through everything before the first hunk. */
  header: string[];
  /** Body lines: the `@@` hunks (and any binary/GIT-binary notice). */
  body: string[];
}

/** Match `diff --git a/<path> b/<path>` and capture both sides. */
const DIFF_GIT_LINE = /^diff --git (?:"?a\/(.+?)"?) (?:"?b\/(.+?)"?)$/;

/**
 * Merge repeated same-file sections of a unified diff into one header +
 * grouped hunks. Returns the input unchanged when it holds no repeated
 * file (the common single-file case) so nothing is reformatted needlessly.
 */
export function coalesceDiffByFile(text: string | null | undefined): string {
  if (!text) return text ?? '';
  // Fast path: a diff with at most one file header cannot repeat a file.
  const firstIdx = text.indexOf('diff --git ');
  if (firstIdx === -1) return text;
  if (text.indexOf('diff --git ', firstIdx + 1) === -1) return text;

  const lines = text.split('\n');
  const preamble: string[] = [];
  const sections: DiffSection[] = [];
  let current: DiffSection | null = null;
  let inBody = false;

  for (const line of lines) {
    if (line.startsWith('diff --git ')) {
      current = { key: sectionKey(line), header: [line], body: [] };
      sections.push(current);
      inBody = false;
      continue;
    }
    if (!current) {
      // Anything before the first `diff --git` (defensive; git diff has none).
      preamble.push(line);
      continue;
    }
    // A hunk marker flips us into the body; everything from there on is a
    // hunk line (context / +add / -remove) or the next hunk's `@@` header.
    if (line.startsWith('@@')) inBody = true;
    if (inBody) current.body.push(line);
    else current.header.push(line);
  }

  // Group by file identity, preserving first-seen order.
  const order: string[] = [];
  const byKey = new Map<string, DiffSection>();
  let repeated = false;
  for (const section of sections) {
    const existing = byKey.get(section.key);
    if (existing) {
      repeated = true;
      existing.body.push(...section.body);
    } else {
      byKey.set(section.key, section);
      order.push(section.key);
    }
  }

  if (!repeated) return text;

  const out: string[] = [...preamble];
  for (const key of order) {
    const section = byKey.get(key)!;
    out.push(...section.header, ...section.body);
  }
  return out.join('\n');
}

/** Grouping key for a `diff --git` line: prefer the b-side (new) path. */
function sectionKey(diffGitLine: string): string {
  const m = DIFF_GIT_LINE.exec(diffGitLine);
  if (m) return m[2] || m[1] || diffGitLine;
  // Unparseable header (unusual quoting/spaces): fall back to the whole
  // line so two identical headers still group and distinct ones do not.
  return diffGitLine;
}
