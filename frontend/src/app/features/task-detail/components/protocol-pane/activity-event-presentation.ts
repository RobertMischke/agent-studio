import type {
  ArtifactImageEvent,
  ConversationEvent,
  SystemParserWarningEvent,
  SystemStatusEvent,
  ToolBurstEvent,
  ToolCommandExecution,
} from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';
import { resolveProtocolImageSrc } from './protocol-image-resolver';

const IMAGE_EXTENSION = /\.(?:avif|gif|jpe?g|png|webp)$/i;
const TOKEN_TOTAL = /\bTurn completed\s*\(tokens:\s*([\d,_]+)\)/i;
const FREE_COMPLETION_LINE = /^\s*(?:Session|Turn) completed(?:\s*\(tokens:\s*[\d,_]+\))?[.!]?\s*$/i;

export interface PresentedToolFile {
  /** Repository-relative path used by the compact and expanded UI. */
  displayPath: string;
  /** Original absolute path retained for the renderer's hover disclosure. */
  fullPath: string;
}

export interface ToolBurstRowPresentation {
  /** Explicit count semantics for the collapsed row. */
  primaryLabel: string;
  /** Top two tool families, already formatted for the compact row. */
  mixLabel: string;
  /** Success aggregate; failures remain separately styleable by the renderer. */
  outcomeLabel: string;
  /** Newline-separated absolute paths for the file-list tooltip. */
  fileTooltip?: string;
}

/**
 * Host-only extension consumed by the next coding-agent-chat row renderer.
 * Version 0.3.2 ignores the extra field but still renders the relative
 * `files` values in its expanded details.
 */
export type PresentedToolBurstEvent = ToolBurstEvent & {
  fileDetails?: readonly PresentedToolFile[];
  rowPresentation: ToolBurstRowPresentation;
};

export interface ActivityEventPresentationOptions {
  typedTurnCompletions?: boolean;
  /** Worktree cwd captured for a specific run index. */
  worktreeRootsByRun?: ReadonlyMap<number, string>;
  /** Used for unscoped legacy tool events. */
  fallbackWorktreeRoot?: string | null;
}

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
  options: ActivityEventPresentationOptions = {},
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
      const worktreeRoot = event.runId == null
        ? options.fallbackWorktreeRoot
        : options.worktreeRootsByRun?.get(event.runId) ?? options.fallbackWorktreeRoot;
      const toolBurst = presentToolBurst(event, worktreeRoot);
      const imageArtifacts = (event.artifacts ?? []).filter((path) => IMAGE_EXTENSION.test(path));
      const otherArtifacts = (event.artifacts ?? []).filter((path) => !IMAGE_EXTENSION.test(path));
      presented.push({ ...toolBurst, artifacts: otherArtifacts.length > 0 ? otherArtifacts : undefined });
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

  return presented;
}

function presentToolBurst(
  event: ToolBurstEvent,
  capturedWorktreeRoot: string | null | undefined,
): PresentedToolBurstEvent {
  // The run record currently captures the CLI cwd. In a monorepo that may be
  // below the actual linked-worktree root, so recover the root from the two
  // canonical pool layouts before stripping it from file paths.
  const worktreeRoot = capturedWorktreeRoot
    ? canonicalWorktreeRoot(capturedWorktreeRoot)
    : null;
  const fileDetails = uniquePresentedFiles(
    event.files?.map((path) => presentToolFile(path, worktreeRoot)),
  );
  const samples = event.samples?.['edit'] && worktreeRoot
    ? { ...event.samples, edit: stripWorktreeRoot(event.samples['edit'], worktreeRoot) }
    : event.samples;
  const files = fileDetails?.map((file) => file.displayPath);

  return {
    ...event,
    files,
    fileDetails,
    samples,
    rowPresentation: toolBurstRowPresentation(event, fileDetails),
  };
}

function toolBurstRowPresentation(
  event: ToolBurstEvent,
  fileDetails: readonly PresentedToolFile[] | undefined,
): ToolBurstRowPresentation {
  const editOnly = event.count > 0 && event.families['edit'] === event.count;
  const operationLabel = editOnly
    ? `${event.count} ${event.count === 1 ? 'Edit' : 'Edits'}`
    : `${event.count} Tool ${event.count === 1 ? 'call' : 'calls'}`;
  const fileLabel = editOnly && fileDetails?.length
    ? ` · ${fileDetails.length} ${fileDetails.length === 1 ? 'file' : 'files'}`
    : '';
  const mixLabel = editOnly ? '' : topToolFamilies(event)
    .map(([family, count]) => `${toolFamilyLabel(family)} ×${count}`)
    .join(', ');

  return {
    primaryLabel: `${operationLabel}${fileLabel}`,
    mixLabel,
    outcomeLabel: event.failures > 0 ? `${event.failures} failed` : 'all ok',
    fileTooltip: fileDetails?.length
      ? fileDetails.map((file) => file.fullPath).join('\n')
      : undefined,
  };
}

function topToolFamilies(event: ToolBurstEvent): [string, number][] {
  const familyOrder = ['command', 'read', 'search', 'edit', 'task', 'todo', 'other'];
  return Object.entries(event.families)
    .filter((entry): entry is [string, number] => Number.isFinite(entry[1]) && entry[1]! > 0)
    .sort((left, right) => right[1] - left[1]
      || familyOrder.indexOf(left[0]) - familyOrder.indexOf(right[0]))
    .slice(0, 2);
}

function toolFamilyLabel(family: string): string {
  return family === 'command' ? 'shell' : family === 'other' ? 'tool' : family;
}

function presentToolFile(path: string, worktreeRoot: string | null): PresentedToolFile {
  const normalized = normalizePath(path);
  const rootedPath = worktreeRoot && !isAbsoluteOrResourcePath(normalized)
    ? `${normalizePath(worktreeRoot).replace(/\/+$/, '')}/${normalized.replace(/^\.\//, '')}`
    : normalized;
  const displayPath = (worktreeRoot ? stripWorktreeRoot(rootedPath, worktreeRoot) : rootedPath)
    .replace(/^\.\//, '');
  return { displayPath, fullPath: rootedPath };
}

function uniquePresentedFiles(
  files: readonly PresentedToolFile[] | undefined,
): PresentedToolFile[] | undefined {
  if (!files) return undefined;
  const seen = new Set<string>();
  return files.filter((file) => {
    const key = /^[a-z]:\//i.test(file.fullPath) ? file.fullPath.toLowerCase() : file.fullPath;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function isAbsoluteOrResourcePath(path: string): boolean {
  return path.startsWith('/') || /^[a-z]:\//i.test(path) || /^[a-z][a-z\d+.-]*:\/\//i.test(path);
}

function canonicalWorktreeRoot(path: string): string {
  const normalized = normalizePath(path).replace(/\/+$/, '');
  const assPool = /^(.*?\/ass-worktrees\/[^/]+\/[^/]+)(?:\/|$)/i.exec(normalized);
  if (assPool) return assPool[1];
  const runnerPool = /^(.*?\/worktrees\/[^/]+)(?:\/|$)/i.exec(normalized);
  return runnerPool?.[1] ?? normalized;
}

function stripWorktreeRoot(value: string, worktreeRoot: string): string {
  const normalized = normalizePath(value);
  const root = normalizePath(worktreeRoot).replace(/\/+$/, '');
  const caseInsensitive = /^[a-z]:\//i.test(root);
  const comparableValue = caseInsensitive ? normalized.toLowerCase() : normalized;
  const comparableRoot = caseInsensitive ? root.toLowerCase() : root;
  const rootIndex = comparableValue.indexOf(`${comparableRoot}/`);
  if (rootIndex < 0) return normalized;
  return normalized.slice(0, rootIndex) + normalized.slice(rootIndex + root.length + 1);
}

function normalizePath(path: string): string {
  return path.trim().replace(/\\/g, '/');
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
