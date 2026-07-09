/**
 * Conversation-projection guard (host-side, pure, library-free).
 *
 * Why this exists
 * ---------------
 * The canonical conversation projection (`parseActivityLog`,
 * `buildConversationTurns`, `projectConversation`) lives in the
 * `coding-agent-chat` library. That projection classifies every unrecognised
 * agent stdout line into a `message` group whose title is the *raw line text*
 * (see the library's `activity-log.parser.ts` fallback branch). When a CLI
 * emits a `stream-json` transport frame that the backend renderer did not
 * turn into a marker line - a new Anthropic event shape, a frame that bypassed
 * the Claude renderer, a partial/duplicated frame - the raw JSON reaches the
 * chat and is rendered verbatim (bug: "Activity-Log zeigt rohes JSON statt
 * Nutzer-Nachrichten").
 *
 * The host owns the seam between the polled CLI output buffer and the
 * projection. This guard sits on that seam: it enforces a WHITELIST of
 * renderable line shapes and guarantees that a raw Anthropic stream-json
 * transport frame is NEVER handed to the projection as chat content. Unknown
 * transport shapes (including brand-new event types we have never seen) are
 * collapsed to a single compact `[internal event]` marker line; the original
 * raw JSON is preserved on {@link CliOutputLine.internalDetail} so the Trace /
 * Verbose-Debug surfaces can still disclose it on demand. It is dropped from
 * the readable chat, never rendered as raw text.
 *
 * This is a defense-in-depth layer. The definitive classification also belongs
 * in the library projection (`conversation-projection.ts` +
 * `conversation-projection.spec.ts` in `C:\Projects\coding-agent-chat`); this
 * host guard makes the app robust regardless of the library version installed.
 *
 * Codex note: the library legitimately parses Codex JSONL frames
 * (`{"type":"item.completed","item":{...}}`) into real tool / message groups.
 * Those are NOT redacted here - the guard only recognises Anthropic
 * `stream-json` / Messages-API transport frames.
 */
import type { CliOutputLine } from '../../../models/task.model';

/** Compact, English (per AGENTS.md) placeholder shown in place of a raw frame. */
export const INTERNAL_EVENT_MARKER = '[internal event]';

/**
 * Anthropic `stream-json` event `type` values that unambiguously identify a
 * transport frame. A JSON object on stdout carrying one of these is always a
 * frame, never chat prose, so it is redacted on the type alone.
 */
const STRONG_STREAM_JSON_TYPES: ReadonlySet<string> = new Set([
  'assistant',
  'tool_use',
  'tool_result',
  'thinking',
  'redacted_thinking',
  'rate_limit_event',
  'message_start',
  'message_delta',
  'message_stop',
  'content_block_start',
  'content_block_delta',
  'content_block_stop',
  'input_json_delta',
  'signature_delta',
]);

/**
 * Event `type` values whose word is generic enough to also appear in unrelated
 * JSON (`system`, `user`, `result`, `error`). These are treated as transport
 * frames only when a Messages-shape sibling key is also present, so an
 * unrelated `{"type":"error","code":5}` snippet is left alone.
 */
const WEAK_STREAM_JSON_TYPES: ReadonlySet<string> = new Set([
  'system',
  'user',
  'result',
  'error',
]);

/**
 * Sibling keys that mark an object as an Anthropic Messages / stream-json
 * transport frame even when its `type` is unknown (a brand-new event shape) or
 * one of the {@link WEAK_STREAM_JSON_TYPES}.
 */
const STREAM_JSON_SHAPE_KEYS: readonly string[] = [
  'message',
  'content',
  'delta',
  'signature',
  'tool_use_id',
  'session_id',
  'subtype',
  'is_error',
  'stop_reason',
  'usage',
];

/** Roles a bare `{ role, content }` Messages envelope may carry. */
const MESSAGE_ENVELOPE_ROLES: ReadonlySet<string> = new Set(['user', 'assistant', 'system']);

/**
 * Codex JSONL frame `type` prefixes. The library owns these (it maps them to
 * tool / message groups), so the guard must never redact them.
 */
const CODEX_FRAME_TYPE_PREFIXES: readonly string[] = ['item.', 'turn.', 'thread.', 'session.'];

/**
 * The whitelist of activity-log group kinds the UI knows how to render. Kept as
 * a plain string list (not the library's `ActivityLogKind` union) so this
 * module stays library-free and independently testable. Any group whose kind
 * falls outside this set is treated as a non-renderable internal event.
 */
export const RENDERABLE_ACTIVITY_KINDS: readonly string[] = [
  'read',
  'search',
  'command',
  'edit',
  'task',
  'todo',
  'error',
  'message',
  'orchestrator',
  'supervisor',
  'other',
];

export function isRenderableActivityKind(kind: string): boolean {
  return RENDERABLE_ACTIVITY_KINDS.includes(kind);
}

/**
 * True when the line text is a raw Anthropic `stream-json` transport frame that
 * must not be rendered as chat content. Returns false for ordinary prose, for
 * JSON snippets that are not transport frames, and for Codex JSONL frames the
 * library handles.
 */
export function isNonRenderableRawLine(text: string | undefined | null): boolean {
  if (!text) return false;
  const trimmed = text.trim();
  // A complete JSON value serialised onto one line. Partial pretty-printed
  // fragments (a lone `{`) are not complete JSON and are intentionally out of
  // scope - they cannot be reliably distinguished from prose in isolation.
  if (trimmed.length < 2) return false;
  const first = trimmed[0];
  if (first !== '{' && first !== '[') return false;
  const last = trimmed[trimmed.length - 1];
  if ((first === '{' && last !== '}') || (first === '[' && last !== ']')) return false;

  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    return false;
  }
  return isTransportFrame(parsed);
}

function isTransportFrame(value: unknown): boolean {
  if (Array.isArray(value)) {
    // A bare content-block array (e.g. a serialised `content: [...]`) is also
    // transport, when at least one element is itself a strong frame part.
    return value.some((element) => isStrongFramePart(element));
  }
  if (!isPlainObject(value)) return false;

  const type = typeof value['type'] === 'string' ? (value['type'] as string) : null;

  // Codex frames belong to the library - never redact them.
  if (type && CODEX_FRAME_TYPE_PREFIXES.some((prefix) => type.startsWith(prefix))) return false;
  if ('item' in value && type && type.startsWith('item')) return false;

  if (type && STRONG_STREAM_JSON_TYPES.has(type)) return true;

  const hasShapeKey = STREAM_JSON_SHAPE_KEYS.some((key) => key in value);

  // Weak/generic type, but shaped like a Messages frame.
  if (type && WEAK_STREAM_JSON_TYPES.has(type) && hasShapeKey) return true;

  // Unknown / brand-new event type that still carries transport structure.
  // This is the case the regression catalog pins: a shape we have never seen
  // must degrade to "[internal event]", never to raw JSON.
  if (type && hasShapeKey) return true;

  // A bare `{ role, content }` Messages envelope with no `type`.
  const role = typeof value['role'] === 'string' ? (value['role'] as string) : null;
  if (role && MESSAGE_ENVELOPE_ROLES.has(role) && 'content' in value) return true;

  return false;
}

/** A single content-block part with a strong transport `type` (`text`,
 * `tool_use`, `tool_result`, `thinking`, ...). Used for array top-levels. */
function isStrongFramePart(value: unknown): boolean {
  if (!isPlainObject(value)) return false;
  const type = typeof value['type'] === 'string' ? (value['type'] as string) : null;
  if (!type) return false;
  if (STRONG_STREAM_JSON_TYPES.has(type)) return true;
  // `text` blocks only count inside an array of parts (they are meaningless as
  // a standalone frame but decisive as a content-block element).
  return type === 'text' && ('text' in value || 'signature' in value);
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Sanitise a CLI-output line buffer for the conversation projection.
 *
 * Every run of consecutive non-renderable transport frames collapses into a
 * single `[internal event]` marker line (keeping the buffer compact when a CLI
 * dumps a burst of frames), with the raw JSON preserved on `internalDetail`.
 * Renderable lines pass through untouched. The input array is returned as-is
 * (same reference) when nothing needed redacting, so downstream memoisation and
 * change detection are not disturbed on the common path.
 */
export function sanitizeProjectionLines(
  lines: readonly CliOutputLine[]
): CliOutputLine[] {
  let anyRedacted = false;
  const out: CliOutputLine[] = [];
  // Non-null while the previous emitted line is an `[internal event]` marker,
  // so a run of consecutive frames folds into that one marker.
  let runDetails: string[] | null = null;

  for (const line of lines) {
    if (isNonRenderableRawLine(line.text)) {
      anyRedacted = true;
      const detail = line.text;
      if (runDetails) {
        // Extend the current marker's run instead of emitting another marker.
        runDetails.push(detail);
        const marker = out[out.length - 1];
        out[out.length - 1] = {
          ...marker,
          internalDetail: joinDetails(runDetails),
        };
        continue;
      }
      runDetails = [detail];
      out.push({
        timestamp: line.timestamp,
        stream: line.stream,
        text: INTERNAL_EVENT_MARKER,
        internalDetail: detail,
      });
      continue;
    }

    runDetails = null;
    out.push(line);
  }

  return anyRedacted ? out : (lines as CliOutputLine[]);
}

/** Cap the disclosed raw detail so a pathological frame burst cannot bloat the
 * DOM; the marker still signals that internal events occurred. */
const MAX_DETAIL_CHARS = 20_000;

function joinDetails(details: readonly string[]): string {
  const joined = details.join('\n');
  if (joined.length <= MAX_DETAIL_CHARS) return joined;
  return `${joined.slice(0, MAX_DETAIL_CHARS)}\n… (${details.length} frames, truncated)`;
}

/** True when the line is a redacted internal-event marker produced above. */
export function isInternalEventLine(line: CliOutputLine): boolean {
  return line.text === INTERNAL_EVENT_MARKER && typeof line.internalDetail === 'string';
}
