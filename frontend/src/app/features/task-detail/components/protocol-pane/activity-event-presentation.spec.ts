import { describe, expect, it } from 'vitest';
import {
  codexTextModeStderrTranscriptFragment,
  projectConversation,
  type ConversationEvent,
  type ToolBurstEvent,
} from 'coding-agent-chat/core';
import { sanitizeProjectionLines } from '../conversation-projection';
import { formatCompactTokens, presentActivityEvents } from './activity-event-presentation';

const range = { source: 'AGT-2088', start: 10, end: 12 };
const burst: ToolBurstEvent = {
  id: 'tools', kind: 'toolBurst', timestamp: '2026-07-11T10:00:00Z', runId: 2, rawRange: range,
  count: 1, failures: 0, durationMs: 20, families: { command: 1 },
};

describe('presentActivityEvents', () => {
  it('folds a parser warning into the preceding tool details', () => {
    const events: ConversationEvent[] = [burst, {
      id: 'warning', kind: 'system.parserWarning', timestamp: '2026-07-11T10:00:01Z', runId: 2,
      rawRange: { ...range, end: 13 }, expectedKind: 'tool-result',
      message: 'Tool router reported exit code 1.', dedupeKey: 'router-1', collapsedByDefault: true,
    }];
    const result = presentActivityEvents(events, 'AGT-2088', null);
    expect(result).toHaveLength(1);
    expect(result[0].kind).toBe('toolBurst');
    expect((result[0] as ToolBurstEvent).commands?.[0].output).toContain('Expected event: tool-result');
  });

  it('promotes image artifacts to named, renderable image rows and keeps other files listed', () => {
    const result = presentActivityEvents([{ ...burst, artifacts: ['results/playwright/dark.png', 'results/report.json'] }], 'AGT-2088', 'watch');
    expect((result[0] as ToolBurstEvent).artifacts).toEqual(['results/report.json']);
    expect(result[1]).toMatchObject({
      kind: 'artifact.image', caption: 'results/playwright / dark.png',
      url: '/api/tasks/AGT-2088/screenshot?path=playwright%2Fdark.png&watchPath=watch',
    });
  });

  it('renders a compact completion total', () => {
    const result = presentActivityEvents([{
      id: 'done', kind: 'system.status', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      category: 'result', label: 'Result', explanation: 'Turn completed (tokens: 14587328)',
    }], 'AGT-2088', null);
    expect(result[0]).toMatchObject({ label: 'Turn completed', explanation: '14,6M tokens' });
    expect(formatCompactTokens(14_587_328)).toBe('14,6M');
  });

  it('removes the synthetic current-task title marker from the activity feed', () => {
    const result = presentActivityEvents([{
      id: 'task', kind: 'taskMarker', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      marker: '4-auto-review', lane: '4-auto-review', jobId: 'AGT-2168',
      title: 'Completion judge: semantically interpret final-attempt prose with typed evidence',
    }, {
      id: 'run', kind: 'runMarker', timestamp: '2026-07-11T10:00:01Z', rawRange: range,
      marker: 'complete', runId: 1,
    }], 'AGT-2088', null);

    expect(result.map((event) => event.kind)).toEqual(['runMarker']);
  });

  it('keeps reissue as decision information instead of a link-like next action', () => {
    const result = presentActivityEvents([{
      id: 'decision', kind: 'decision.orchestrator', timestamp: '2026-07-11T10:00:02Z',
      rawRange: range, decisionType: 'reissue', reason: 'One more pass is needed.',
      action: 'reissue',
    }], 'AGT-2088', null);

    expect(result[0]).toMatchObject({
      kind: 'decision.orchestrator',
      decisionType: 'reissue',
      reason: 'One more pass is needed.',
      action: undefined,
    });
  });

  it('projects a Codex stderr transcript as neutral evidence plus the complete agent answer', () => {
    const events = projectConversation({
      source: 'AGT-2168',
      lines: sanitizeProjectionLines(codexTextModeStderrTranscriptFragment()),
    });

    expect(events.filter((event) =>
      event.kind === 'system.status' && event.label === 'CLI failed')).toHaveLength(0);
    expect(events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'codex-transcript',
      label: 'Codex transcript',
      severity: 'info',
    }));
    expect(events).toContainEqual(expect.objectContaining({
      kind: 'message.taskAgent',
      body: expect.stringContaining('Its second line is preserved in that same turn.'),
    }));
  });
});
