import { CliOutputLine } from '../models/job.model';

export type ActivityLogKind = 'read' | 'search' | 'command' | 'edit' | 'task' | 'todo' | 'error' | 'message' | 'orchestrator' | 'other';
export type ActivityLogFilters = Record<ActivityLogKind, boolean>;

export interface ActivityLogGroup {
  id: string;
  kind: ActivityLogKind;
  title: string;
  subtitle: string;
  status: 'ok' | 'error' | 'neutral';
  lines: CliOutputLine[];
  collapsedByDefault: boolean;
}

const actionStartRegex = /^(?<marker>[^\w\s]+|x|X|\*)\s+(?<label>.+)$/i;

export const activityLogKinds: ActivityLogKind[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'orchestrator', 'other'];

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
  other: true
};

export function parseActivityLog(lines: CliOutputLine[]): ActivityLogGroup[] {
  const groups: ActivityLogGroup[] = [];
  let current: ActivityLogGroup | null = null;

  for (const line of lines) {
    // User follow-ups are persisted with stream='user' (see backend
    // TaskRunnerService.AppendUserPromptToCliLog). They are always their own
    // group — never folded into a preceding agent action — so the chat
    // transcript reads as alternating user/agent turns.
    if (line.stream === 'user') {
      current = {
        id: `${groups.length}-${line.timestamp}-user`,
        kind: 'message',
        title: line.text,
        subtitle: '',
        status: 'neutral',
        lines: [line],
        collapsedByDefault: false
      };
      groups.push(current);
      // Reset so any subsequent continuation/blank lines don't fold into
      // the user message group.
      current = null;
      continue;
    }

    // Orchestrator meta messages are written by the backend's
    // OrchestratorChatLog. They are first-class chat participants alongside
    // USER and AGENT, never folded into adjacent agent activity. Their text
    // already carries a leading [tag] (decision / reissue / heuristic /
    // giveup) which we keep as the title so the renderer can pick a glyph.
    if (line.stream === 'orchestrator') {
      current = {
        id: `${groups.length}-${line.timestamp}-orchestrator`,
        kind: 'orchestrator',
        title: line.text,
        subtitle: '',
        status: 'neutral',
        lines: [line],
        collapsedByDefault: false
      };
      groups.push(current);
      current = null;
      continue;
    }

    const action = parseActionLine(line);
    if (action) {
      current = {
        id: `${groups.length}-${line.timestamp}-${action.title}`,
        kind: action.kind,
        title: action.title,
        subtitle: '',
        status: action.status,
        lines: [line],
        collapsedByDefault: false
      };
      groups.push(current);
      continue;
    }

    if (isBlank(line.text)) {
      if (current) current.lines.push(line);
      continue;
    }

    if (current && isContinuation(line.text)) {
      current.lines.push(line);
      if (!current.subtitle) {
        current.subtitle = cleanContinuation(line.text);
      }
      if (line.stream === 'stderr' || /error|failed|exited with error/i.test(line.text)) {
        current.status = 'error';
      }
      continue;
    }

    const kind: ActivityLogKind = line.stream === 'stderr' || /error|failed|exited with error/i.test(line.text)
      ? 'error'
      : 'message';
    current = {
      id: `${groups.length}-${line.timestamp}-message`,
      kind,
      title: line.text,
      subtitle: '',
      status: kind === 'error' ? 'error' : 'neutral',
      lines: [line],
      collapsedByDefault: false
    };
    groups.push(current);
  }

  return compressActivityGroups(groups);
}

export function filterActivityGroups(groups: ActivityLogGroup[], filters: ActivityLogFilters): ActivityLogGroup[] {
  return groups.filter((group) => filters[group.kind]);
}

export function flattenActivityLines(groups: ActivityLogGroup[]): CliOutputLine[] {
  return groups.flatMap((group) => group.lines);
}

export type ChatRole = 'agent' | 'tool' | 'system' | 'user' | 'orchestrator';

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

const TOOL_KINDS: ReadonlyArray<ActivityLogKind> = ['read', 'search', 'command', 'edit', 'task', 'todo'];

export function buildChatMessages(groups: ActivityLogGroup[]): ChatMessage[] {
  return groups.map((group, index) => groupToChatMessage(group, index));
}

function groupToChatMessage(group: ActivityLogGroup, index: number): ChatMessage {
  const isTool = TOOL_KINDS.includes(group.kind);
  const isError = group.kind === 'error' || group.status === 'error';
  const isUser = group.lines.length > 0 && group.lines[0].stream === 'user';
  const isOrchestrator = group.kind === 'orchestrator'
    || (group.lines.length > 0 && group.lines[0].stream === 'orchestrator');
  const role: ChatRole = isOrchestrator ? 'orchestrator'
    : isUser ? 'user'
    : isError && !isTool ? 'system'
    : isTool ? 'tool'
    : 'agent';

  const firstLine = group.lines[0];
  const timestamp = firstLine ? firstLine.timestamp : new Date().toISOString();

  const author = isOrchestrator
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

// =================================================================
// Conversation turn builder (Activity Log "Conversation" mode)
// =================================================================
//
// The Conversation view collapses the raw activity stream into the kind of
// alternating dialogue a human reader expects:
//
//   user -> tool burst (collapsed) -> agent text turn -> tool burst -> ...
//
// One "turn" is a contiguous run of same-role groups - so a sequence of 12
// reads + 3 edits becomes a single tool burst with counts ("12 reads, 3
// edits"), and a sequence of 4 agent message lines becomes one big readable
// agent turn whose body is rendered as Markdown. This is the structure the
// user explicitly asked for: hide tool noise, keep responses prominent and
// legible.

export type ConversationTurnKind = 'agent' | 'user' | 'tools' | 'system' | 'orchestrator';

export interface ToolBurstSummary {
  total: number;
  counts: Partial<Record<ActivityLogKind, number>>;
  /**
   * One example label per kind (e.g. "Read prompt.md") so the collapsed badge
   * can show what was actually done without expanding the full list.
   */
  samples: Partial<Record<ActivityLogKind, string>>;
  /**
   * Wall-clock span from the burst's first action line to its last, in
   * milliseconds. The Conversation view shows it as a small "· 4s" chip so
   * the reader gets a sense of how long the tool noise took without it
   * stealing focus from the agent reply. Zero when the burst spans a single
   * timestamp or timestamps are missing.
   */
  durationMs: number;
}

export interface ConversationTurn {
  id: string;
  kind: ConversationTurnKind;
  timestamp: string;
  status: 'ok' | 'error' | 'neutral';
  /** Source groups, kept so the UI can offer "expand the underlying tools" or copy. */
  groups: ActivityLogGroup[];
  /**
   * For agent / user / system turns this is the joined raw text. It is fed
   * through {@link renderMarkdown} on the view side (we keep this layer free
   * of HTML so it stays unit-testable as plain strings).
   */
  text: string;
  /** Populated only for kind === 'tools'. */
  toolSummary?: ToolBurstSummary;
}

function isToolKind(kind: ActivityLogKind): boolean {
  return TOOL_KINDS.includes(kind);
}

/**
 * `[taskboard]`-prefixed lines on the system stream are runtime markers
 * (CLI started, CLI exited, duration, exit code, model). They belong in
 * the Trace view as run-bookkeeping but they crowd out the actual agent
 * reply in the Conversation view. The Conversation view filters them
 * out; the metadata strip above the activity log is the right place
 * for "duration: 65s, model: claude-opus-4-7" if we surface them at all.
 */
function isTaskboardRuntimeMarker(group: ActivityLogGroup): boolean {
  if (group.lines.length === 0) return false;
  const first = group.lines[0];
  if (first.stream !== 'system') return false;
  return /^\s*\[taskboard\]/i.test(first.text ?? '');
}

/**
 * Watchdog meta lines arrive as orchestrator messages tagged `[watchdog]`.
 * They drive the watchdog chip in the protocol-pane header; surfacing them
 * in the Conversation view as well would double-up the user feedback.
 * Filtered out here, kept in Trace.
 */
function isWatchdogMetaLine(group: ActivityLogGroup): boolean {
  if (group.lines.length === 0) return false;
  const first = group.lines[0];
  if (first.stream !== 'orchestrator') return false;
  return /\[watchdog\]/i.test(first.text ?? '');
}

/**
 * Maps a sequence of {@link ActivityLogGroup}s into a sequence of conversation
 * turns. Adjacent groups of the same role are merged. Errors that aren't
 * tool errors surface as their own `system` turns so they're never buried
 * inside an agent block. Runtime taskboard markers (CLI started / exited
 * / duration) are filtered out; they live in the Trace view only.
 */
export function buildConversationTurns(groups: ActivityLogGroup[]): ConversationTurn[] {
  const turns: ConversationTurn[] = [];
  const filtered = groups.filter((g) => !isTaskboardRuntimeMarker(g) && !isWatchdogMetaLine(g));
  let i = 0;
  while (i < filtered.length) {
    const group = filtered[i];
    const role = roleFor(group);

    // Collect the contiguous run of same-role groups.
    const run: ActivityLogGroup[] = [group];
    i += 1;
    while (i < filtered.length && roleFor(filtered[i]) === role) {
      run.push(filtered[i]);
      i += 1;
    }

    turns.push(turnFromRun(run, role, turns.length));
  }
  return turns;
}

function roleFor(group: ActivityLogGroup): ConversationTurnKind {
  const isUser = group.lines.length > 0 && group.lines[0].stream === 'user';
  if (isUser) return 'user';
  if (group.kind === 'orchestrator'
    || (group.lines.length > 0 && group.lines[0].stream === 'orchestrator')) return 'orchestrator';
  if (isToolKind(group.kind)) return 'tools';
  if (group.kind === 'error' || group.status === 'error') return 'system';
  return 'agent';
}

function turnFromRun(run: ActivityLogGroup[], kind: ConversationTurnKind, index: number): ConversationTurn {
  const firstLine = run[0]?.lines[0];
  const timestamp = firstLine ? firstLine.timestamp : new Date().toISOString();
  const status: 'ok' | 'error' | 'neutral' = run.some((g) => g.status === 'error')
    ? 'error'
    : kind === 'user'
      ? 'neutral'
      : 'ok';

  if (kind === 'tools') {
    return {
      id: `turn-${index}-tools`,
      kind,
      timestamp,
      status,
      groups: run,
      text: '',
      toolSummary: summarizeToolBurst(run)
    };
  }

  return {
    id: `turn-${index}-${kind}`,
    kind,
    timestamp,
    status,
    groups: run,
    text: turnTextFromGroups(run, kind)
  };
}

/**
 * Joins a run of agent / user / system groups into the readable text body of
 * a single turn. We use group titles (the first line of each group) rather
 * than the entire `lines` array to avoid reintroducing tool-output noise that
 * the parser already classified as continuation. Blank lines between titles
 * are preserved as paragraph breaks so the Markdown renderer can pick them
 * up as `<p>` boundaries.
 */
function turnTextFromGroups(run: ActivityLogGroup[], kind: ConversationTurnKind): string {
  const segments: string[] = [];
  for (const group of run) {
    if (kind === 'user') {
      segments.push(group.title);
      continue;
    }
    // For agent / system turns, the model's text was emitted as a sequence of
    // lines that the backend split per newline. Re-join them with single
    // newlines so paragraph structure (blank line = new <p>) survives.
    const lines = group.lines.map((l) => l.text).filter((t) => t !== undefined);
    segments.push(lines.join('\n'));
  }
  return segments.join('\n\n').trim();
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
    case 'other': return 'Other';
  }
}

function parseActionLine(line: CliOutputLine): { kind: ActivityLogKind; title: string; status: 'ok' | 'error' | 'neutral' } | null {
  const match = actionStartRegex.exec(line.text);
  if (!match?.groups) return null;

  const label = match.groups['label'].trim();
  const marker = match.groups['marker'];
  const status = line.stream === 'stderr' || marker.toLowerCase() === 'x' || /exited with error|failed/i.test(label)
    ? 'error'
    : 'ok';

  return {
    kind: classifyAction(label, status),
    title: label,
    status
  };
}

function classifyAction(label: string, status: 'ok' | 'error' | 'neutral'): ActivityLogKind {
  if (status === 'error') return 'error';
  if (/^Read\b/i.test(label)) return 'read';
  if (/^Search\b/i.test(label)) return 'search';
  if (/\(shell\)|^Run\b|^Execute|^Executing|^Build|^Check\b/i.test(label)) return 'command';
  if (/^Edit\b|^Write\b|^Create\b|^Delete\b|^Move\b|^Update\b|^Apply\b/i.test(label)) return 'edit';
  if (/^Task\b/i.test(label)) return 'task';
  if (/^Todo\b/i.test(label)) return 'todo';
  return 'other';
}

function compressActivityGroups(groups: ActivityLogGroup[]): ActivityLogGroup[] {
  const output: ActivityLogGroup[] = [];
  let index = 0;

  while (index < groups.length) {
    const group = groups[index];
    if (!isCompressible(group)) {
      output.push(group);
      index += 1;
      continue;
    }

    const batch = [group];
    index += 1;
    while (index < groups.length && groups[index].kind === group.kind && groups[index].status === group.status) {
      batch.push(groups[index]);
      index += 1;
    }

    if (batch.length === 1) {
      output.push(group);
      continue;
    }

    const lines = batch.flatMap((item) => item.lines);
    output.push({
      id: `${group.id}-batch-${batch.length}`,
      kind: group.kind,
      title: `${activityKindLabel(group.kind)} ×${batch.length}`,
      subtitle: batch.map((item) => item.subtitle || item.title).filter(Boolean).slice(0, 3).join(', '),
      status: group.status,
      lines,
      collapsedByDefault: true
    });
  }

  return output;
}

// Tool kinds whose adjacent runs collapse into a single weighted batch group
// (title "Reading files ×3"). Read and search are the noisiest, but command,
// edit, task, and todo bursts surface the same "wall of repeated entries"
// problem in the trace view, so they collapse too. Non-tool kinds (message,
// error, orchestrator) keep their individual entries; their content matters.
function isCompressible(group: ActivityLogGroup): boolean {
  return TOOL_KINDS.includes(group.kind);
}

function isContinuation(text: string): boolean {
  return /^\s/.test(text) || /^[|`\\/_-]/.test(text);
}

function cleanContinuation(text: string): string {
  return text.replace(/^[\s|`\\/_-]+/, '').trim();
}

function isBlank(text: string): boolean {
  return text.trim().length === 0;
}
