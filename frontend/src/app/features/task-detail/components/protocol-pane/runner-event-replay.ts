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
    label: record.kind === 'turn.completed' ? 'Turn completed' : 'Session completed',
    explanation: '',
  };
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
  const prefix = record.code?.trim() ? `[${record.code.trim()}] ` : '';
  return `${prefix}${record.message?.trim() || 'Runner diagnostic'}`;
}
