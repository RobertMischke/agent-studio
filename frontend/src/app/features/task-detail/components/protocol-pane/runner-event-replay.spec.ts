import { describe, expect, it } from 'vitest';
import type { RunnerRecordedEvent } from '../../../run-timeline';
import { mergeReplayEvents, projectRunnerReplay } from './runner-event-replay';

const records: RunnerRecordedEvent[] = [
  {
    id: 'session-1', kind: 'session.started', timestamp: '2026-07-22T10:00:00Z',
    sessionId: 'session-full', runIndex: 1, cli: 'codex', model: 'gpt-5.4', thinkingLevel: 'high',
  },
  {
    id: 'warning-1', kind: 'diagnostic', timestamp: '2026-07-22T10:00:01Z',
    severity: 'warning', code: 'plugin-empty', message: 'Plugin returned an empty payload.',
  },
  {
    id: 'turn-1', kind: 'turn.completed', timestamp: '2026-07-22T10:00:02Z',
    runIndex: 1, inputTokens: 74_192, outputTokens: 8_331, durationMs: 412_000,
  },
];

describe('projectRunnerReplay', () => {
  it('maps lifecycle events to CAC types and keeps diagnostics out of the main feed', () => {
    const result = projectRunnerReplay(records, 'AGT-2149');

    expect(result.timelineEvents.map(event => event.kind)).toEqual([
      'runMarker', 'system.status', 'metric.token',
    ]);
    expect(result.timelineEvents[1]).toMatchObject({ label: 'Turn completed', explanation: '' });
    expect(result.timelineEvents[2]).toMatchObject({ scope: 'turn', inputTokens: 74_192, outputTokens: 8_331 });
    expect(JSON.stringify(result.timelineEvents)).not.toContain('Plugin returned');
    expect(result.diagnosticLines).toEqual([expect.objectContaining({
      stream: 'diagnostic', text: '[plugin-empty] Plugin returned an empty payload.',
    })]);
  });

  it('merges the typed replay chronologically with projected conversation events', () => {
    const replay = projectRunnerReplay(records, 'AGT-2149').timelineEvents;
    const merged = mergeReplayEvents([{
      id: 'message', kind: 'message.taskAgent', timestamp: '2026-07-22T10:00:01.500Z',
      actor: 'Agent', body: 'Implementation is complete.',
      rawRange: { source: 'AGT-2149', start: 1, end: 1 },
    }], replay);
    expect(merged.map(event => event.id)).toEqual(['session-1', 'message', 'turn-1', 'turn-1:usage']);
  });
});
