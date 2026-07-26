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
  options: { typedTurnCompletions?: boolean } = {},
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

  return presented;
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
