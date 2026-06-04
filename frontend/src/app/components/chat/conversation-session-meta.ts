/**
 * Pure helpers for the conversation session / rate-limit meta block
 * (`Frontend:NextGenChat`).
 *
 * The CLI streams two lifecycle lines per run that the operator wants
 * surfaced as a compact meta card rather than raw bullet text:
 *
 *   ● Session init <uuid>
 *   ● Rate limit · five-hour · allowed · reset in 4.4 h  [window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]
 *
 * `parseRateLimit` lifts the structured `[key=value ...]` payload (plus the
 * human "reset in X" hint) out of the raw line so the card can render a
 * window pill, status, and a real reset clock. Everything here is pure
 * TypeScript: no Angular, no DOM, no service calls.
 */

export interface ParsedRateLimit {
  /** Raw window token, e.g. `five_hour`. */
  window?: string;
  /** Friendly window label, e.g. `5h` or `Weekly`. */
  windowLabel?: string;
  /** Status token, e.g. `allowed`, `limited`. */
  status?: string;
  /** Reset time as epoch milliseconds, when `resetsAt` was present. */
  resetsAtMs?: number;
  /** Human reset hint pulled from the line ("reset in 4.4 h"). */
  resetHint?: string;
  /** Verbatim source line, kept for the tooltip. */
  raw: string;
}

export interface SessionCardData {
  sessionIdFull?: string;
  sessionIdShort?: string;
  /** ISO timestamp of the captured `Session init` line. */
  initAt?: string;
  rateLimit?: ParsedRateLimit;
}

const WINDOW_LABELS: Record<string, string> = {
  five_hour: '5h',
  'five-hour': '5h',
  weekly: 'Weekly',
  week: 'Weekly',
  monthly: 'Monthly',
  month: 'Monthly',
  daily: 'Daily',
  day: 'Daily',
};

export function windowLabelFor(window: string | undefined): string | undefined {
  if (!window) return undefined;
  const key = window.toLowerCase();
  if (WINDOW_LABELS[key]) return WINDOW_LABELS[key];
  return key
    .replace(/[_-]+/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

/**
 * Parse a captured `● Rate limit ...` line into structured fields. Tolerant
 * of missing pieces: a line with no `[...]` payload still yields the human
 * `resetHint` when present, and `raw` is always set.
 */
export function parseRateLimit(raw: string): ParsedRateLimit {
  const out: ParsedRateLimit = { raw: raw.trim() };

  const bracket = /\[([^\]]*)\]/.exec(raw);
  if (bracket) {
    for (const token of bracket[1].split(/\s+/)) {
      const eq = token.indexOf('=');
      if (eq <= 0) continue;
      const key = token.slice(0, eq);
      const value = token.slice(eq + 1);
      if (key === 'window') out.window = value;
      else if (key === 'status') out.status = value;
      else if (key === 'resetsAt') {
        const secs = Number(value);
        if (Number.isFinite(secs) && secs > 0) out.resetsAtMs = secs * 1000;
      }
    }
  }

  // "reset in 4.4 h" — captured before the bracket payload starts.
  const hint = /reset\s+in\s+([^[]+?)(?:\s{2,}|\s*\[|$)/i.exec(raw);
  if (hint) out.resetHint = `reset in ${hint[1].trim()}`;

  out.windowLabel = windowLabelFor(out.window);
  return out;
}
