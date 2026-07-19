/**
 * App-side activity-log helpers.
 *
 * The canonical activity-log grouper (`parseActivityLog`) and the
 * conversation-turn builder (`buildConversationTurns`) moved into
 * `coding-agent-chat/core` during the library extraction; they are
 * re-exported here so existing app imports keep working. What remains in
 * this file is the app-specific layer the library intentionally does not
 * own: filter state, the legacy chat-message mapping, the live-status
 * indicator, tool-burst rollups for the Activity Log view, and the
 * orchestrator `[steer]` line parser.
 */
import {
  parseActivityLog as parseActivityLogLib,
  buildConversationTurns,
  type ActivityLogGroup,
  type ActivityLogKind,
} from 'coding-agent-chat/core';
import { CliOutputLine } from '../../../models/task.model';
import { sanitizeProjectionLines } from './conversation-projection';

/**
 * Host wrapper around the library grouper. It runs the conversation-projection
 * guard over the raw line buffer FIRST, so a raw stream-json transport frame
 * can never reach the library's fallback branch (which would otherwise render
 * the raw JSON as a `message` group title). Every app surface consumes this
 * wrapper - the legacy activity-log Conversation/Trace views, the live-status
 * derivation below, and the protocol pane's `buildConversationTurns` call - so
 * the guard applies uniformly. See {@link sanitizeProjectionLines}.
 */
export function parseActivityLog(lines: CliOutputLine[]): ActivityLogGroup[] {
  return parseActivityLogLib(sanitizeProjectionLines(lines));
}

export {
  buildConversationTurns,
  type ActivityLogGroup,
  type ActivityLogKind,
};
export { sanitizeProjectionLines, INTERNAL_EVENT_MARKER, isInternalEventLine } from './conversation-projection';

/**
 * The library exports `buildConversationTurns` but keeps its row types
 * internal; derive them from the function signature so the app-side view
 * models stay exactly in sync with what the builder returns.
 */
export type ConversationTurn = ReturnType<typeof buildConversationTurns>[number];
export type ConversationTurnKind = ConversationTurn['kind'];
export type ToolBurstSummary = NonNullable<ConversationTurn['toolSummary']>;

export type ActivityLogFilters = Record<ActivityLogKind, boolean>;

export const activityLogKinds: ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'orchestrator', 'supervisor', 'other'];

export const defaultActivityLogFilters: ActivityLogFilters = {
  read: true,
  search: true,
  command: true,
  edit: true,
  task: true,
  todo: true,
  error: true,
  message: true,
  orchestrator: true,
  supervisor: true,
  other: true
};

export function filterActivityGroups(groups: ActivityLogGroup[], filters: ActivityLogFilters): ActivityLogGroup[] {
  return groups.filter((group) => filters[group.kind]);
}

export function flattenActivityLines(groups: ActivityLogGroup[]): CliOutputLine[] {
  return groups.flatMap((group) => group.lines);
}

export type ChatRole = 'agent' | 'tool' | 'system' | 'user' | 'orchestrator' | 'supervisor';

export interface ChatMessage {
  id: string;
  role: ChatRole;
  author: string;
  avatar: string;
  kindLabel: string;
  title: string;
  subtitle: string;
  status: 'ok' | 'error' | 'neutral';
  timestamp: string;
  body: CliOutputLine[];
  collapsedByDefault: boolean;
}

const TOOL_KINDS: readonly ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo'];

export function buildChatMessages(groups: ActivityLogGroup[]): ChatMessage[] {
  return groups.map((group, index) => groupToChatMessage(group, index));
}

function groupToChatMessage(group: ActivityLogGroup, index: number): ChatMessage {
  const isTool = TOOL_KINDS.includes(group.kind);
  const isError = group.kind === 'error' || group.status === 'error';
  const isUser = group.lines.length > 0 && group.lines[0].stream === 'user';
  const isOrchestrator = group.kind === 'orchestrator'
    || (group.lines.length > 0 && group.lines[0].stream === 'orchestrator');
  const isSupervisor = group.kind === 'supervisor'
    || (group.lines.length > 0 && group.lines[0].stream === 'supervisor');
  const role: ChatRole = isSupervisor ? 'supervisor'
    : isOrchestrator ? 'orchestrator'
    : isUser ? 'user'
    : isError && !isTool ? 'system'
    : isTool ? 'tool'
    : 'agent';

  const firstLine = group.lines[0];
  const timestamp = firstLine ? firstLine.timestamp : new Date().toISOString();

  const author = isSupervisor
    ? 'Supervisor'
    : isOrchestrator
    ? 'Orchestrator'
    : isUser
      ? 'You'
      : isError && !isTool
        ? 'System'
        : isTool
          ? 'Tool call'
          : 'Agent';

  const avatar = isOrchestrator
    ? '⚙'
    : isUser
      ? '🧑'
      : isError && !isTool
        ? '!'
        : isTool
          ? toolAvatarFor(group.kind)
          : '🤖';

  const kindLabel = isTool ? activityKindLabel(group.kind) : (isError ? 'Error' : '');

  return {
    id: `chat-${index}-${group.id}`,
    role,
    author,
    avatar,
    kindLabel,
    title: group.title,
    subtitle: group.subtitle,
    status: group.status,
    timestamp,
    body: group.lines,
    collapsedByDefault: isTool || group.collapsedByDefault
  };
}

export function summarizeToolBurst(groups: ActivityLogGroup[]): ToolBurstSummary {
  const counts: Partial<Record<ActivityLogKind, number>> = {};
  const samples: Partial<Record<ActivityLogKind, string>> = {};
  let total = 0;
  let firstMs = Number.POSITIVE_INFINITY;
  let lastMs = Number.NEGATIVE_INFINITY;
  for (const group of groups) {
    // The parser pre-compresses runs of same-kind tool actions into a batch
    // group with title "Reading files ×3"; inferBatchSize recovers the
    // original count from that suffix. Non-batched groups count as 1.
    const batchSize = inferBatchSize(group);
    counts[group.kind] = (counts[group.kind] ?? 0) + batchSize;
    total += batchSize;
    if (!samples[group.kind]) {
      samples[group.kind] = sampleLabelFor(group);
    }
    for (const l of group.lines) {
      const t = Date.parse(l.timestamp);
      if (!Number.isFinite(t)) continue;
      if (t < firstMs) firstMs = t;
      if (t > lastMs) lastMs = t;
    }
  }
  const durationMs = Number.isFinite(firstMs) && Number.isFinite(lastMs) && lastMs > firstMs
    ? lastMs - firstMs
    : 0;
  return { total, counts, samples, durationMs };
}

// =================================================================
// Live status (the "agent is working" indicator at the bottom of the chat)
// =================================================================
//
// While the run is active the user wants a constant signal of life: a
// pulsing indicator with a short label that says what the agent is
// doing right now ("Reading prompt.md", "Searching for foo",
// "Thinking..."). The label is derived from the most recent meaningful
// activity-log group; the elapsed-since-last-line lets the user see
// that something is still ticking even when the agent stalls between
// tool calls.

export type LiveStatusKind =
  | 'starting'
  | 'tool'
  | 'agent'
  | 'user'
  | 'orchestrator'
  | 'recovering';

export interface LiveStatus {
  kind: LiveStatusKind;
  /** Short verb phrase ("Reading", "Searching", "Thinking"). */
  verb: string;
  /** Optional target/detail ("prompt.md", "needle", "src/foo.ts"); empty when not applicable. */
  detail: string;
  /**
   * Milliseconds since the last log line. Drives the "· 4s" chip and
   * gives the user a sense of "it's still going" when the agent has
   * been silent for a while.
   */
  sinceMs: number;
}

/**
 * Derive a live-status indicator from the rolling output buffer. Returns
 * `null` when the run is not active (the indicator should not render).
 *
 * The function is intentionally synchronous and pure so it can be unit
 * tested without a component harness; the caller is responsible for
 * supplying `nowMs` (the wall clock) and for re-evaluating the result
 * on whatever cadence is appropriate (the activity-log view ticks once
 * per second so the elapsed counter feels alive).
 */
export function deriveLiveStatus(
  lines: CliOutputLine[],
  isRunning: boolean,
  nowMs: number
): LiveStatus | null {
  if (!isRunning) return null;
  if (lines.length === 0) {
    return { kind: 'starting', verb: 'Starting agent', detail: '', sinceMs: 0 };
  }

  // Walk back to the last non-blank line so a trailing newline does not
  // freeze the status at "0s" forever.
  let lastIdx = lines.length - 1;
  while (lastIdx >= 0 && (!lines[lastIdx].text || lines[lastIdx].text.trim() === '')) {
    lastIdx -= 1;
  }
  if (lastIdx < 0) {
    return { kind: 'starting', verb: 'Starting agent', detail: '', sinceMs: 0 };
  }

  const lastLine = lines[lastIdx];
  const lastMs = Date.parse(lastLine.timestamp);
  const sinceMs = Number.isFinite(lastMs) ? Math.max(0, nowMs - lastMs) : 0;

  const groups = parseActivityLog(lines);
  // Skip purely runtime/bookkeeping groups - they aren't what the user
  // means by "what is the agent doing now".
  let lastGroup: ActivityLogGroup | null = null;
  for (let i = groups.length - 1; i >= 0; i--) {
    const g = groups[i];
    if (isLiveStatusNoise(g)) continue;
    lastGroup = g;
    break;
  }
  if (!lastGroup) {
    return { kind: 'agent', verb: 'Thinking', detail: '', sinceMs };
  }

  if (lastGroup.lines[0]?.stream === 'user') {
    return { kind: 'user', verb: 'Working on your message', detail: '', sinceMs };
  }
  if (lastGroup.kind === 'orchestrator') {
    return { kind: 'orchestrator', verb: 'Orchestrator deciding', detail: '', sinceMs };
  }
  if (lastGroup.kind === 'error') {
    return { kind: 'recovering', verb: 'Recovering from error', detail: '', sinceMs };
  }

  switch (lastGroup.kind) {
    case 'read':
      return { kind: 'tool', verb: 'Reading', detail: extractTargetLabel(lastGroup, 'file'), sinceMs };
    case 'search':
      return { kind: 'tool', verb: 'Searching', detail: extractTargetLabel(lastGroup, 'query'), sinceMs };
    case 'edit':
      return { kind: 'tool', verb: 'Editing', detail: extractTargetLabel(lastGroup, 'file'), sinceMs };
    case 'command':
      return { kind: 'tool', verb: 'Running', detail: extractTargetLabel(lastGroup, 'command'), sinceMs };
    case 'task':
      return { kind: 'tool', verb: 'Delegating', detail: extractTargetLabel(lastGroup, 'task'), sinceMs };
    case 'todo':
      return { kind: 'tool', verb: 'Updating todos', detail: '', sinceMs };
    case 'message':
    case 'other':
    default:
      return { kind: 'agent', verb: 'Thinking', detail: '', sinceMs };
  }
}

/**
 * `[taskboard]`-prefixed lines on the system stream are runtime markers
 * (CLI started, CLI exited, duration, exit code, model). They belong in
 * the Trace view as run-bookkeeping, not in the live status.
 */
function isTaskboardRuntimeMarker(group: ActivityLogGroup): boolean {
  if (group.lines.length === 0) return false;
  const first = group.lines[0];
  if (first.stream !== 'system') return false;
  return /^\s*\[taskboard\]/i.test(first.text ?? '');
}

/**
 * Watchdog meta lines arrive as orchestrator messages tagged
 * `[watchdog]` (legacy) or `[watchdog-warning]` / `[watchdog-timeout]`
 * (operator-friendly form). They drive the watchdog chip in the
 * protocol-pane header; they are not "current activity".
 */
function isWatchdogMetaLine(group: ActivityLogGroup): boolean {
  if (group.lines.length === 0) return false;
  const first = group.lines[0];
  if (first.stream !== 'orchestrator') return false;
  return /\[watchdog[^\]]*\]/i.test(first.text ?? '');
}

function isLiveStatusNoise(group: ActivityLogGroup): boolean {
  if (isTaskboardRuntimeMarker(group)) return true;
  if (isWatchdogMetaLine(group)) return true;
  // A blank-only group has nothing to say about current activity.
  if (group.lines.every((l) => !l.text || l.text.trim() === '')) return true;
  return false;
}

const LIVE_VERB_PREFIX_RE =
  /^(Read|Reading|Search|Searching|Grep|Edit|Editing|Write|Writing|Run|Running|Build|Building|Check|Checking|Update|Updating|Apply|Applying|Move|Moving|Delete|Deleting|Create|Creating|Execute|Executing|Task|Todo)\b\s*[-:(]?\s*/i;

/**
 * Pulls the operand out of an action title so the live status reads
 * "Editing src/foo.ts" instead of repeating the verb. Handles batched
 * titles ("Reading files ×3" -> "3 files") and Claude's "Read(path)"
 * shape. Long paths collapse to a tail so the row stays one line.
 */
function extractTargetLabel(group: ActivityLogGroup, batchNoun: string): string {
  const batched = /×\s*(\d+)\s*$/.exec(group.title);
  if (batched) {
    const n = Number(batched[1]);
    return n === 1 ? `1 ${batchNoun}` : `${n} ${pluralize(batchNoun)}`;
  }
  let detail = group.title.trim();
  detail = detail.replace(LIVE_VERB_PREFIX_RE, '');
  // Strip wrapping () or quotes that some CLI drivers emit (Read(path), Search "needle").
  detail = detail.replace(/^[("'`]+/, '').replace(/[)"'`]+$/, '');
  // Collapse internal whitespace runs.
  detail = detail.replace(/\s+/g, ' ').trim();
  if (detail.length > 64) {
    detail = '...' + detail.slice(-61);
  }
  return detail;
}

function pluralize(noun: string): string {
  if (noun.endsWith('y')) return `${noun.slice(0, -1)}ies`;
  if (noun.endsWith('s')) return noun;
  return `${noun}s`;
}

/**
 * Compact "since" formatter for the live-status row. Aims for the
 * shortest readable form: "" for sub-second values (don't show), then
 * "4s", "47s", "1m 12s", "1h 5m". Used by the activity-log view.
 */
export function formatLiveSince(ms: number): string {
  if (!Number.isFinite(ms) || ms < 1500) return '';
  const totalSec = Math.round(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const totalMin = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  if (totalMin < 60) return sec === 0 ? `${totalMin}m` : `${totalMin}m ${sec}s`;
  const hr = Math.floor(totalMin / 60);
  const min = totalMin % 60;
  return min === 0 ? `${hr}h` : `${hr}h ${min}m`;
}

/**
 * Compact human label for a tool-burst duration. Aimed at the small grey chip
 * in the Conversation view: "<1s", "4s", "1m 20s", "12m". Anything north of
 * an hour collapses to "Nh Mm" so the chip stays narrow.
 */
export function formatBurstDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '';
  if (ms < 1000) return '<1s';
  const totalSec = Math.round(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const totalMin = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  if (totalMin < 60) return sec === 0 ? `${totalMin}m` : `${totalMin}m ${sec}s`;
  const hr = Math.floor(totalMin / 60);
  const min = totalMin % 60;
  return min === 0 ? `${hr}h` : `${hr}h ${min}m`;
}

/**
 * Re-bins the underlying groups of a tool burst by kind so the expanded view
 * shows one collapsed-per-kind block (e.g. "Read ×12" with the file list
 * underneath) instead of repeating the same kind label dozens of times. Each
 * bin keeps the source group references so the detail rows can still link
 * back to the original action labels.
 */
export interface ToolBurstBin {
  kind: ActivityLogKind;
  count: number;
  groups: ActivityLogGroup[];
}

export function binToolBurstByKind(groups: ActivityLogGroup[]): ToolBurstBin[] {
  const order: ActivityLogKind[] = [];
  const map = new Map<ActivityLogKind, ToolBurstBin>();
  for (const group of groups) {
    const batchSize = inferBatchSize(group);
    let bin = map.get(group.kind);
    if (!bin) {
      bin = { kind: group.kind, count: 0, groups: [] };
      map.set(group.kind, bin);
      order.push(group.kind);
    }
    bin.count += batchSize;
    bin.groups.push(group);
  }
  return order.map((k) => map.get(k)!);
}

// Compressed batch titles carry a trailing weight, e.g. "Reading files ×3".
// The legacy "(3)" suffix is still accepted so a stale buffer does not lose
// its count after the format change.
const BATCH_COUNT_RE = /\s*(?:×(\d+)|\((\d+)\))\s*$/;

function inferBatchSize(group: ActivityLogGroup): number {
  const m = BATCH_COUNT_RE.exec(group.title);
  if (m) return Math.max(1, Number(m[1] ?? m[2]));
  return 1;
}

function sampleLabelFor(group: ActivityLogGroup): string {
  if (group.subtitle) return group.subtitle;
  return group.title.replace(BATCH_COUNT_RE, '').trimEnd();
}

function toolAvatarFor(kind: ActivityLogKind): string {
  switch (kind) {
    case 'read': return '📖';
    case 'search': return '🔎';
    case 'command': return '⚙';
    case 'edit': return '✎';
    case 'task': return '◆';
    case 'todo': return '☐';
    default: return '⚙';
  }
}

export function activityKindLabel(kind: ActivityLogKind): string {
  switch (kind) {
    case 'read': return 'Reading files';
    case 'search': return 'Searches';
    case 'command': return 'Commands';
    case 'edit': return 'Edits';
    case 'task': return 'Tasks';
    case 'todo': return 'Todos';
    case 'error': return 'Errors';
    case 'message': return 'Messages';
    case 'orchestrator': return 'Orchestrator';
    case 'supervisor': return 'Supervisor';
    case 'other': return 'Other';
  }
}

/**
 * Parsed shape of a `[steer]` orchestrator chat line.
 *
 * The backend writes the orchestrator's STEER reply as one Markdown line
 * with `**Need:** ... **Why:** ... **Options:** A) ... | B) ...` segments
 * (see `OrchestratorReplyParser.FormatSteerForChat`). The frontend's
 * conversation view recovers the structure so it can render distinct
 * controls (option buttons, screenshot affordance) instead of dumping the
 * raw line into a generic orchestrator pill.
 *
 * Returns `null` when the line is not a steer line. A line counts as a
 * steer line when it carries the leading `[steer]` tag the chat-log
 * persisted; the leading bracket may be preceded by a stream prefix
 * `[orchestrator]` from the persisted log shape.
 */
export interface ParsedSteer {
  need: string;
  why: string;
  options: string[];
  /** True when the parsed Need text mentions a screenshot - drives the upload affordance. */
  needsScreenshot: boolean;
}

const STEER_TAG_RE = /\[steer\]\s*/i;
const STEER_NEED_RE = /\*\*Need:\*\*\s*([^*]+?)(?=\s*\*\*|$)/i;
const STEER_WHY_RE = /\*\*Why:\*\*\s*([^*]+?)(?=\s*\*\*|$)/i;
const STEER_OPTIONS_RE = /\*\*Options:\*\*\s*(.+?)$/i;
const STEER_OPTION_ITEM_RE = /(?:^|\|)\s*(?:[A-Za-z][).]|\d+[).]|-)\s*([^|]+)/g;

export function parseOrchestratorSteer(text: string): ParsedSteer | null {
  if (!text || !STEER_TAG_RE.test(text)) return null;
  // The persisted log line carries a redundant `[orchestrator]` segment
  // because the chat-log call site prefixes its own message with
  // `[orchestrator]` and the writer adds a stream tag of the same name.
  // Strip any occurrences of either tag before pulling fields.
  const body = text
    .replace(STEER_TAG_RE, ' ')
    .replace(/\[orchestrator\]/gi, ' ')
    .trim();
  if (!body) return null;

  const needMatch = STEER_NEED_RE.exec(body);
  const need = needMatch ? needMatch[1].trim() : '';
  if (!need) return null;

  const whyMatch = STEER_WHY_RE.exec(body);
  const why = whyMatch ? whyMatch[1].trim() : '';

  const optionsBlock = STEER_OPTIONS_RE.exec(body);
  const options: string[] = [];
  if (optionsBlock) {
    const block = optionsBlock[1];
    let m: RegExpExecArray | null;
    STEER_OPTION_ITEM_RE.lastIndex = 0;
    while ((m = STEER_OPTION_ITEM_RE.exec(block)) !== null) {
      const opt = m[1].trim();
      if (opt) options.push(opt);
    }
  }

  return {
    need,
    why,
    options,
    needsScreenshot: /screenshot|screen\s*shot|image|picture/i.test(need)
  };
}
