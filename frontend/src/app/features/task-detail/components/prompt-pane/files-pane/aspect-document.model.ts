/**
 * Client model + tolerant parser for the structured `aspect-{id}.json`
 * artefact the backend's `AspectRunnerService` now writes as the source of
 * truth (concept doc §5 — "one JSON source, two renderings"). The Files tab
 * renders this structurally (meta header + status badge + collapsible
 * details) instead of dumping the frontmatter-laden markdown twin.
 *
 * Backward compatible by construction: legacy runs wrote only the markdown
 * (`aspect-{id}.md`), so {@link parseAspectDocument} returns `null` for
 * anything that is not a JSON object carrying the load-bearing `aspect` +
 * `status` fields, and the caller falls back to the markdown renderer.
 *
 * The wire shape mirrors the backend `AspectDocument` record; keep the two
 * in sync (see `backend/Features/Runner/AspectVerdict.cs`).
 */
export interface AspectDocument {
  /** Wire-format version; forward-compat only, not required to render. */
  schemaVersion?: number;
  /** Aspect identifier, e.g. `code-quality`. */
  aspect: string;
  /** Normalised status token: `pass` | `concerns` | `block`. */
  status: string;
  /** One-line summary the aspect produced. */
  summary: string;
  /** The model's narrative reply (freetext / light markdown). */
  details: string;
  /** ISO-8601 UTC write time, when present. */
  createdAt?: string | null;
  /** Model id that produced the verdict, when known. */
  model?: string | null;
  /** Concern tag id (`{namespace}:concerns`) or null on pass. */
  tag?: string | null;
  /** Optional extensible metric map (files-changed / tests-passed …). */
  metrics?: Record<string, string> | null;
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/**
 * Parse a raw `aspect-{id}.json` body into an {@link AspectDocument}.
 * Returns `null` for empty input, non-JSON (e.g. a legacy markdown file), a
 * non-object payload, or a payload missing the load-bearing `aspect` /
 * `status` fields — so the Files tab can safely fall back to markdown.
 */
export function parseAspectDocument(raw: string | null | undefined): AspectDocument | null {
  if (!raw) return null;
  // Cheap pre-check: a markdown twin starts with `---` frontmatter or `#`,
  // never `{`. Avoids a try/catch on every legacy aspect file.
  const head = raw.trimStart();
  if (!head.startsWith('{')) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;

  const rec = parsed as Record<string, unknown>;
  const aspect = asString(rec['aspect']).trim();
  const status = asString(rec['status']).trim().toLowerCase();
  if (!aspect || !status) return null;

  let metrics: Record<string, string> | null = null;
  const rawMetrics = rec['metrics'];
  if (rawMetrics && typeof rawMetrics === 'object' && !Array.isArray(rawMetrics)) {
    const collected: Record<string, string> = {};
    for (const [key, value] of Object.entries(rawMetrics as Record<string, unknown>)) {
      if (typeof value === 'string') collected[key] = value;
      else if (typeof value === 'number' || typeof value === 'boolean') collected[key] = String(value);
    }
    if (Object.keys(collected).length > 0) metrics = collected;
  }

  return {
    schemaVersion: typeof rec['schemaVersion'] === 'number' ? rec['schemaVersion'] : undefined,
    aspect,
    status,
    summary: asString(rec['summary']).trim(),
    details: asString(rec['details']),
    createdAt: typeof rec['createdAt'] === 'string' ? rec['createdAt'] : null,
    model: typeof rec['model'] === 'string' ? rec['model'] : null,
    tag: typeof rec['tag'] === 'string' ? rec['tag'] : null,
    metrics,
  };
}
