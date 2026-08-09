import type {
  ArtifactImageEvent,
  ConversationEvent,
  SupervisorWaitEvent,
  SystemParserWarningEvent,
  SystemStatusEvent,
  ToolFamily,
  ToolBurstEvent,
  ToolCommandExecution,
} from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';
import { resolveProtocolImageSrc } from './protocol-image-resolver';

const IMAGE_EXTENSION = /\.(?:avif|gif|jpe?g|png|webp)$/i;
const TOKEN_TOTAL = /\bTurn completed\s*\(tokens:\s*([\d,_]+)\)/i;
const FREE_COMPLETION_LINE = /^\s*(?:Session|Turn) completed(?:\s*\(tokens:\s*[\d,_]+\))?[.!]?\s*$/i;
const TERMINAL_WAIT_REASON = /\b(?:timed out|timeout reached|killed after|auto-cancelled after)\b/i;
const REPOSITORY_SEGMENT = /(?:^|\/)(\.agents|backend(?:\.Tests)?|docs|frontend|prompts|runner|scripts)(\/.*)?$/i;

const TOOL_FAMILY_ORDER: readonly ToolFamily[] = [
  'command', 'read', 'search', 'edit', 'task', 'todo', 'other',
];

export interface ActivityPresentationOptions {
  typedTurnCompletions?: boolean;
  worktreeRoot?: string | null;
  worktreeRootsByRun?: Readonly<Record<number, string>>;
  commitDiffRunIds?: readonly number[];
}

export interface ActivitySummaryPresentation {
  kind: 'tool' | 'edit';
  fullPaths: readonly string[];
  relativePaths: readonly string[];
  action?: 'commit-diff';
}

export type ActivitySummaryEvent = SystemStatusEvent & {
  activityPresentation: ActivitySummaryPresentation;
};

/** Remove transport-era completion prose when the typed lifecycle is authoritative. */
export function stripLegacyCompletionLines(
  lines: readonly CliOutputLine[],
  typedLifecycle: boolean,
): CliOutputLine[] {
  if (!typedLifecycle) return [...lines];
  return lines.filter(line => !FREE_COMPLETION_LINE.test(line.text));
}

/**
 * Product-specific cleanup between the shared conversation projector and the
 * embedded Activity view. It keeps parser diagnostics attached to the tool
 * operation they describe and promotes result images to renderable evidence.
 */
export function presentActivityEvents(
  events: readonly ConversationEvent[],
  jobId: string,
  watchPath: string | null | undefined,
  options: ActivityPresentationOptions = {},
): ConversationEvent[] {
  const presented: ConversationEvent[] = [];

  for (const event of events) {
    // projectConversation appends the open task itself as a final marker.
    // In a task-local Activity feed that repeats the surrounding card title
    // without representing a transition, which made it look like an
    // unexplained lane event. Real run and lane evidence remains untouched.
    if (event.kind === 'taskMarker') {
      continue;
    }

    if (event.kind === 'system.parserWarning') {
      const burstIndex = findOwningBurst(presented, event);
      if (burstIndex >= 0) {
        presented[burstIndex] = attachParserDetail(presented[burstIndex] as ToolBurstEvent, event);
        continue;
      }
    }

    if (event.kind === 'toolBurst') {
      const imageArtifacts = (event.artifacts ?? []).filter((path) => IMAGE_EXTENSION.test(path));
      const otherArtifacts = (event.artifacts ?? []).filter((path) => !IMAGE_EXTENSION.test(path));
      presented.push({ ...event, artifacts: otherArtifacts.length > 0 ? otherArtifacts : undefined });
      presented.push(...imageArtifacts.map((path, index) => artifactImage(event, path, index, jobId, watchPath)));
      continue;
    }

    if (
      event.kind === 'decision.orchestrator'
      && event.decisionType.toLowerCase() === 'reissue'
      && event.action?.toLowerCase() === 'reissue'
    ) {
      presented.push({ ...event, action: undefined });
      continue;
    }

    if (event.kind === 'system.status' && TOKEN_TOTAL.test(`${event.label} ${event.explanation}`)) {
      if (!options.typedTurnCompletions) presented.push(...formatCompletion(event));
      continue;
    }

    presented.push(event);
  }

  return foldSupervisorWaits(presented.map((event) =>
    event.kind === 'toolBurst' ? presentToolBurst(event, watchPath, options) : event));
}

export function isActivitySummaryEvent(event: ConversationEvent): event is ActivitySummaryEvent {
  return event.kind === 'system.status' && 'activityPresentation' in event;
}

function foldSupervisorWaits(events: readonly ConversationEvent[]): ConversationEvent[] {
  const folded: ConversationEvent[] = [];
  let pending: SupervisorWaitEvent[] = [];

  const flush = (): void => {
    if (pending.length === 0) return;
    folded.push(pending.length === 1 ? pending[0] : supervisorSummary(pending));
    pending = [];
  };

  for (const event of events) {
    if (event.kind !== 'supervisor.wait') {
      flush();
      folded.push(event);
      continue;
    }

    if (isTerminalWait(event)) {
      flush();
      folded.push(event);
      continue;
    }

    pending.push(event);
  }

  flush();
  return folded;
}

function isTerminalWait(event: SupervisorWaitEvent): boolean {
  return event.state === 'killed' || TERMINAL_WAIT_REASON.test(event.reason ?? '');
}

function supervisorSummary(events: readonly SupervisorWaitEvent[]): SupervisorWaitEvent {
  const first = events[0];
  const last = events[events.length - 1];
  const longest = events.reduce((current, event) =>
    event.quietSeconds > current.quietSeconds ? event : current, first);
  const quietCount = events.filter((event) => event.state === 'quiet').length;
  const resumedCount = events.filter((event) => event.state === 'resumed').length;
  const stateLabel = quietCount > 0 && resumedCount > 0
    ? 'quiet/resumed'
    : quietCount > 0 ? 'quiet' : 'resumed';
  const stateCounts = quietCount > 0 && resumedCount > 0
    ? ` (${quietCount} quiet, ${resumedCount} resumed)`
    : '';
  const budget = suspiciousBudget(longest.reason)
    ?? events.map((event) => suspiciousBudget(event.reason)).find((value) => value !== null)
    ?? null;
  const allowed = budget === null ? '' : ` (allowed ${formatSeconds(budget)})`;

  return {
    ...last,
    id: `${first.id}:summary:${last.id}`,
    timestamp: last.timestamp,
    rawRange: {
      source: first.rawRange.source,
      start: Math.min(...events.map((event) => event.rawRange.start)),
      end: Math.max(...events.map((event) => event.rawRange.end)),
    },
    quietSeconds: longest.quietSeconds,
    lastOutputRange: longest.lastOutputRange,
    reason: `${events.length}× ${stateLabel}${stateCounts} · longest silence ${formatSeconds(longest.quietSeconds)}${allowed} · last ${formatClock(last.timestamp)}`,
  };
}

function suspiciousBudget(reason: string | undefined): number | null {
  if (!reason) return null;
  const match = /\ballowed\s*[=:]?\s*([0-9]+(?:\.[0-9]+)?)(?:\s*\/\s*[0-9]+(?:\.[0-9]+)?)?\s*s\b/i.exec(reason)
    ?? /\bbudget\s*[=:]?\s*([0-9]+(?:\.[0-9]+)?)\s*s\b/i.exec(reason);
  if (!match) return null;
  const value = Number(match[1]);
  return Number.isFinite(value) ? value : null;
}

function presentToolBurst(
  event: ToolBurstEvent,
  watchPath: string | null | undefined,
  options: ActivityPresentationOptions,
): ActivitySummaryEvent {
  const failures = Math.max(0, event.failures ?? 0);
  const success = failures === 0 ? 'all ok' : `${failures} failed`;
  const duration = formatDuration(event.durationMs ?? 0);
  const editCount = event.families?.edit ?? 0;
  const isEdit = editCount > 0 && editCount === event.count;
  const worktreeRoot = event.runId === undefined
    ? options.worktreeRoot
    : options.worktreeRootsByRun?.[event.runId] ?? options.worktreeRoot;
  const fullPaths = unique([
    ...(event.files ?? []),
    ...(isEdit ? editPathsFromSamples(event.samples?.['edit']) : []),
  ]);
  const relativePaths = unique(fullPaths.map((path) => repoRelativePath(path, worktreeRoot, watchPath)));

  if (isEdit) {
    const fileCount = relativePaths.length;
    const fileLabel = `${fileCount} ${fileCount === 1 ? 'file' : 'files'}`;
    const pathSummary = relativePaths.length === 0
      ? 'file list unavailable'
      : relativePaths.length === 1
        ? relativePaths[0]
        : `${relativePaths[0]} +${relativePaths.length - 1} more`;
    return {
      ...event,
      id: `${event.id}:edit-summary`,
      kind: 'system.status',
      category: 'activity-edit-summary',
      severity: failures > 0 ? 'error' : 'info',
      label: `${editCount} ${editCount === 1 ? 'Edit' : 'Edits'} · ${fileLabel}`,
      explanation: `${pathSummary} · ${success} · ${duration}`,
      nextStep: undefined,
      activityPresentation: {
        kind: 'edit',
        fullPaths,
        relativePaths,
        action: event.runId !== undefined && options.commitDiffRunIds?.includes(event.runId)
          ? 'commit-diff'
          : undefined,
      },
    };
  }

  return {
    ...event,
    id: `${event.id}:tool-summary`,
    kind: 'system.status',
    category: 'activity-tool-summary',
    severity: failures > 0 ? 'error' : 'info',
    label: `${event.count} Tool ${event.count === 1 ? 'call' : 'calls'}`,
    explanation: `${toolMix(event)} · ${success} · ${duration}`,
    nextStep: undefined,
    activityPresentation: {
      kind: 'tool',
      fullPaths,
      relativePaths,
    },
  };
}

function editPathsFromSamples(sample: string | undefined): string[] {
  if (!sample) return [];
  const paths: string[] = [];
  const pattern = /(?:^|,\s*)Edit\s+(.+?)(?=,\s*Edit\s+|$)/gi;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(sample)) !== null) {
    const path = match[1].trim();
    if (path) paths.push(path);
  }
  return paths;
}

function toolMix(event: ToolBurstEvent): string {
  const entries = TOOL_FAMILY_ORDER
    .map((family) => ({ family, count: event.families?.[family] ?? 0 }))
    .filter(({ count }) => count > 0)
    .sort((a, b) => b.count - a.count || TOOL_FAMILY_ORDER.indexOf(a.family) - TOOL_FAMILY_ORDER.indexOf(b.family))
    .slice(0, 2);
  return entries.length > 0
    ? entries.map(({ family, count }) => `${toolFamilyLabel(family)} ×${count}`).join(', ')
    : 'tool mix unavailable';
}

function toolFamilyLabel(family: ToolFamily): string {
  return family === 'command' ? 'shell' : family;
}

function repoRelativePath(
  path: string,
  worktreeRoot: string | null | undefined,
  watchPath: string | null | undefined,
): string {
  const normalized = path.replace(/\\/g, '/').replace(/\/{2,}/g, '/');
  if (!isAbsolutePath(normalized)) return normalized.replace(/^\.\//, '');

  for (const root of [worktreeRoot, watchPath]) {
    const relative = stripRoot(normalized, root);
    if (relative !== null) return relative;
  }

  const worktreeMatch = /\/(?:ass-worktrees|worktrees)\/[^/]+\/(.+)$/i.exec(normalized);
  if (worktreeMatch) return worktreeMatch[1];

  const repositoryMatch = REPOSITORY_SEGMENT.exec(normalized);
  if (repositoryMatch) return `${repositoryMatch[1]}${repositoryMatch[2] ?? ''}`;

  return normalized.split('/').pop() ?? normalized;
}

function stripRoot(path: string, root: string | null | undefined): string | null {
  if (!root) return null;
  const normalizedRoot = root.replace(/\\/g, '/').replace(/\/{2,}/g, '/').replace(/\/$/, '');
  const windowsPath = /^[a-z]:\//i.test(path) || /^[a-z]:\//i.test(normalizedRoot);
  const comparablePath = windowsPath ? path.toLowerCase() : path;
  const comparableRoot = windowsPath ? normalizedRoot.toLowerCase() : normalizedRoot;
  if (comparablePath === comparableRoot) return path.split('/').pop() ?? path;
  if (!comparablePath.startsWith(`${comparableRoot}/`)) return null;
  return path.slice(normalizedRoot.length + 1);
}

function isAbsolutePath(path: string): boolean {
  return path.startsWith('/') || /^[a-z]:\//i.test(path);
}

function unique(values: readonly string[]): string[] {
  return [...new Set(values.filter((value) => value.trim().length > 0))];
}

function formatDuration(milliseconds: number): string {
  const totalSeconds = Math.max(0, Math.round(milliseconds / 1_000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
}

function formatSeconds(seconds: number): string {
  return `${Math.round(seconds)}s`;
}

function formatClock(timestamp: string): string {
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) return timestamp;
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function findOwningBurst(events: readonly ConversationEvent[], warning: SystemParserWarningEvent): number {
  for (let index = events.length - 1; index >= 0; index -= 1) {
    const candidate = events[index];
    if (candidate.runId !== warning.runId) break;
    if (candidate.kind === 'toolBurst') return index;
    if (candidate.kind.startsWith('message.') || candidate.kind === 'runMarker') break;
  }
  return -1;
}

function attachParserDetail(burst: ToolBurstEvent, warning: SystemParserWarningEvent): ToolBurstEvent {
  const detail: ToolCommandExecution = {
    command: 'Parser detail',
    status: 'unknown',
    exitCode: null,
    output: `${warning.message}\nExpected event: ${warning.expectedKind}`,
    outputLineCount: 2,
    outputTruncated: false,
  };
  return {
    ...burst,
    rawRange: { ...burst.rawRange, end: Math.max(burst.rawRange.end, warning.rawRange.end) },
    commands: [...(burst.commands ?? []), detail],
  };
}

function artifactImage(
  burst: ToolBurstEvent,
  path: string,
  index: number,
  jobId: string,
  watchPath: string | null | undefined,
): ArtifactImageEvent {
  const normalized = path.replace(/\\/g, '/');
  const fileName = normalized.split('/').pop() || normalized;
  const folder = normalized.slice(0, Math.max(0, normalized.lastIndexOf('/'))) || 'results';
  return {
    id: `${burst.id}:artifact:${index}`,
    kind: 'artifact.image',
    timestamp: burst.timestamp,
    runId: burst.runId,
    model: burst.model,
    thinkingLevel: burst.thinkingLevel,
    rawRange: burst.rawRange,
    caption: `${folder} / ${fileName}`,
    sourcePath: normalized,
    durablePath: normalized.startsWith('results/') ? normalized : null,
    sourceTool: 'agent',
    url: resolveProtocolImageSrc(normalized, jobId, watchPath),
  };
}

function formatCompletion(event: SystemStatusEvent): ConversationEvent[] {
  const match = TOKEN_TOTAL.exec(`${event.label} ${event.explanation}`);
  if (!match) return [event];
  const total = Number(match[1].replace(/[, _]/g, ''));
  if (!Number.isFinite(total)) return [event];
  return [
    {
      ...event,
      category: 'result',
      label: 'Turn completed',
      explanation: '',
      nextStep: undefined,
    },
    {
      id: `${event.id}:usage`,
      kind: 'metric.token',
      timestamp: event.timestamp,
      runId: event.runId,
      model: event.model,
      thinkingLevel: event.thinkingLevel,
      rawRange: event.rawRange,
      scope: 'turn',
      inputTokens: total,
      outputTokens: 0,
    },
  ];
}

export function formatCompactTokens(value: number): string {
  if (value < 1_000) return new Intl.NumberFormat('de-DE').format(value);
  const divisor = value >= 1_000_000 ? 1_000_000 : 1_000;
  const suffix = value >= 1_000_000 ? 'M' : 'k';
  return `${new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1 }).format(value / divisor)}${suffix}`;
}
