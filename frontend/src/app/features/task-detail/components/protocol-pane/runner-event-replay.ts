import type {
  ConversationEvent,
  MetricTokenEvent,
  RawLineRange,
  RunMarkerEvent,
  SystemStatusEvent,
} from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';
import type { RunnerRecordedEvent } from '../../../run-timeline';

export interface RunnerReplayProjection {
  timelineEvents: ConversationEvent[];
  diagnosticLines: CliOutputLine[];
}

/** Origin marker the server assigns to public-demo replay events. */
export const SIMULATED_ORIGIN = 'simulated';

/**
 * True when the event was replayed into a demo instance from a signed fixed
 * trace rather than produced by a real run. Every surface that renders a runner
 * event has to say so, so the check lives here instead of per component.
 */
export function isSimulated(record: Pick<RunnerRecordedEvent, 'origin'>): boolean {
  return record.origin?.trim().toLowerCase() === SIMULATED_ORIGIN;
}

/**
 * Adapt the released runner wire events to CAC's published event model.
 * Lifecycle labels remain structured events; warnings are deliberately kept
 * out of the main feed and exposed only as Trace lines.
 */
export function projectRunnerReplay(
  records: readonly RunnerRecordedEvent[] | null | undefined,
  source: string,
): RunnerReplayProjection {
  const timelineEvents: ConversationEvent[] = [];
  const diagnosticLines: CliOutputLine[] = [];

  [...(records ?? [])]
    .sort((a, b) => a.timestamp.localeCompare(b.timestamp) || a.id.localeCompare(b.id))
    .forEach((record, index) => {
      const rawRange: RawLineRange = { source: `${source}:runner-events`, start: index + 1, end: index + 1 };
      if (record.kind === 'diagnostic') {
        diagnosticLines.push({
          timestamp: record.timestamp,
          stream: 'diagnostic',
          text: diagnosticText(record),
        });
        return;
      }

      if (record.kind === 'session.started' || record.kind === 'turn.started') {
        timelineEvents.push(startMarker(record, rawRange));
        return;
      }

      timelineEvents.push(completionStatus(record, rawRange));
      if (record.kind === 'turn.completed' && hasUsage(record)) {
        timelineEvents.push(tokenMetric(record, rawRange));
      }
    });

  return { timelineEvents, diagnosticLines };
}

export function mergeReplayEvents(
  conversation: readonly ConversationEvent[],
  replay: readonly ConversationEvent[],
): ConversationEvent[] {
  return [...conversation, ...replay].sort(
    (a, b) => a.timestamp.localeCompare(b.timestamp) || a.id.localeCompare(b.id),
  );
}

function startMarker(record: RunnerRecordedEvent, rawRange: RawLineRange): RunMarkerEvent {
  return {
    id: record.id,
    kind: 'runMarker',
    timestamp: record.timestamp,
    rawRange,
    runId: record.runIndex ?? undefined,
    marker: 'start',
    cli: record.cli,
    model: record.model,
    thinkingLevel: record.thinkingLevel,
    sessionId: record.sessionId,
  };
}

function completionStatus(record: RunnerRecordedEvent, rawRange: RawLineRange): SystemStatusEvent {
  return {
    id: record.id,
    kind: 'system.status',
    timestamp: record.timestamp,
    rawRange,
    runId: record.runIndex ?? undefined,
    model: record.model,
    thinkingLevel: record.thinkingLevel,
    category: 'result',
    label: completionLabel(record),
    explanation: '',
  };
}

function completionLabel(record: RunnerRecordedEvent): string {
  const label = record.kind === 'turn.completed' ? 'Turn completed' : 'Session completed';
  return isSimulated(record) ? `Simulated ${label.toLowerCase()}` : label;
}

function tokenMetric(record: RunnerRecordedEvent, rawRange: RawLineRange): MetricTokenEvent {
  return {
    id: `${record.id}:usage`,
    kind: 'metric.token',
    timestamp: record.timestamp,
    rawRange,
    runId: record.runIndex ?? undefined,
    model: record.model,
    thinkingLevel: record.thinkingLevel,
    scope: 'turn',
    inputTokens: record.inputTokens ?? 0,
    outputTokens: record.outputTokens ?? 0,
    reasoningTokens: record.reasoningTokens ?? undefined,
  };
}

function hasUsage(record: RunnerRecordedEvent): boolean {
  return record.inputTokens != null || record.outputTokens != null || record.reasoningTokens != null;
}

function diagnosticText(record: RunnerRecordedEvent): string {
  const origin = isSimulated(record) ? '[simulated] ' : '';
  const prefix = record.code?.trim() ? `[${record.code.trim()}] ` : '';
  return `${origin}${prefix}${record.message?.trim() || 'Runner diagnostic'}`;
}
