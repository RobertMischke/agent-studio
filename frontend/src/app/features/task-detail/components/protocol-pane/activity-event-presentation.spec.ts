import { describe, expect, it } from 'vitest';
import {
  codexTextModeStderrTranscriptFragment,
  projectConversation,
  type ConversationEvent,
  type SupervisorWaitEvent,
  type ToolBurstEvent,
} from 'coding-agent-chat/core';
import { sanitizeProjectionLines } from '../conversation-projection';
import {
  formatCompactTokens,
  presentActivityEvents,
  stripLegacyCompletionLines,
} from './activity-event-presentation';

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
    expect(result[0]).toMatchObject({ kind: 'system.status', category: 'activity-tool-summary' });
    expect((result[0] as unknown as ToolBurstEvent).commands?.[0].output).toContain('Expected event: tool-result');
  });

  it('promotes image artifacts to named, renderable image rows and keeps other files listed', () => {
    const result = presentActivityEvents([{ ...burst, artifacts: ['results/playwright/dark.png', 'results/report.json'] }], 'AGT-2088', 'watch');
    expect((result[0] as ToolBurstEvent).artifacts).toEqual(['results/report.json']);
    expect(result[1]).toMatchObject({
      kind: 'artifact.image', caption: 'results/playwright / dark.png',
      url: '/api/tasks/AGT-2088/screenshot?path=playwright%2Fdark.png&watchPath=watch',
    });
  });

  it('upgrades legacy completion prose into a typed label and turn metric', () => {
    const result = presentActivityEvents([{
      id: 'done', kind: 'system.status', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      category: 'result', label: 'Result', explanation: 'Turn completed (tokens: 14587328)',
    }], 'AGT-2088', null);
    expect(result[0]).toMatchObject({ label: 'Turn completed', explanation: '' });
    expect(result[1]).toMatchObject({ kind: 'metric.token', scope: 'turn', inputTokens: 14_587_328 });
    expect(JSON.stringify(result)).not.toContain('Turn completed (tokens:');
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

  it('drops legacy completion prose when a typed runner completion exists', () => {
    const result = presentActivityEvents([{
      id: 'done', kind: 'system.status', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      category: 'result', label: 'Result', explanation: 'Turn completed (tokens: 1200)',
    }], 'AGT-2088', null, { typedTurnCompletions: true });
    expect(result).toEqual([]);
  });

  it('removes free completion transport lines only when typed lifecycle events exist', () => {
    const lines = [
      { timestamp: '2026-07-11T10:00:00Z', stream: 'stdout', text: 'Turn completed (tokens: 1,200)' },
      { timestamp: '2026-07-11T10:00:01Z', stream: 'stdout', text: 'Session completed.' },
      { timestamp: '2026-07-11T10:00:02Z', stream: 'stdout', text: 'Implementation completed the turn safely.' },
    ];

    expect(stripLegacyCompletionLines(lines, true).map(line => line.text)).toEqual([
      'Implementation completed the turn safely.',
    ]);
    expect(stripLegacyCompletionLines(lines, false)).toEqual(lines);
  });

  it('folds a consecutive supervisor quiet/resumed sequence into one last-timestamp summary', () => {
    const waits: SupervisorWaitEvent[] = [
      wait('quiet-1', '2026-07-11T10:00:00Z', 'quiet', 35, 20),
      wait('resumed-1', '2026-07-11T10:00:05Z', 'resumed', 0, 21),
      wait('quiet-2', '2026-07-11T10:01:00Z', 'quiet', 48, 22),
      wait('resumed-2', '2026-07-11T10:01:08Z', 'resumed', 0, 23),
    ];

    const result = presentActivityEvents(waits, 'AGT-2088', null);

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      kind: 'supervisor.wait',
      timestamp: '2026-07-11T10:01:08Z',
      quietSeconds: 48,
      rawRange: { source: 'AGT-2088', start: 20, end: 23 },
    });
    expect((result[0] as SupervisorWaitEvent).reason).toContain('4× quiet/resumed (2 quiet, 2 resumed)');
    expect((result[0] as SupervisorWaitEvent).reason).toContain('longest silence 48s (allowed 180s)');
    expect((result[0] as SupervisorWaitEvent).reason).toContain('last ');
  });

  it('ends a supervisor sequence at every non-supervisor event', () => {
    const message: ConversationEvent = {
      id: 'agent', kind: 'message.taskAgent', timestamp: '2026-07-11T10:00:30Z', rawRange: range,
      actor: 'Agent', body: 'Still working.',
    };
    const result = presentActivityEvents([
      wait('quiet-1', '2026-07-11T10:00:00Z', 'quiet', 35, 20),
      wait('resumed-1', '2026-07-11T10:00:05Z', 'resumed', 0, 21),
      message,
      wait('quiet-2', '2026-07-11T10:01:00Z', 'quiet', 45, 22),
      wait('resumed-2', '2026-07-11T10:01:08Z', 'resumed', 0, 23),
    ], 'AGT-2088', null);

    expect(result.map((event) => event.kind)).toEqual([
      'supervisor.wait', 'message.taskAgent', 'supervisor.wait',
    ]);
    expect((result[0] as SupervisorWaitEvent).reason).toContain('2× quiet/resumed');
    expect((result[2] as SupervisorWaitEvent).reason).toContain('2× quiet/resumed');
  });

  it('never folds killed or timeout supervisor events', () => {
    const killed = wait('killed', '2026-07-11T10:02:00Z', 'killed', 600, 24);
    const timeout = {
      ...wait('timeout', '2026-07-11T10:03:00Z', 'quiet', 600, 25),
      reason: '[watchdog-timeout] timeout reached after 600s',
      severity: 'error' as const,
    };
    const result = presentActivityEvents([
      wait('quiet-1', '2026-07-11T10:00:00Z', 'quiet', 35, 20),
      wait('resumed-1', '2026-07-11T10:00:05Z', 'resumed', 0, 21),
      killed,
      timeout,
    ], 'AGT-2088', null);

    expect(result).toHaveLength(3);
    expect(result[1]).toBe(killed);
    expect(result[2]).toBe(timeout);
    expect(result[1]).toMatchObject({ state: 'killed', severity: 'error' });
  });

  it('projects successful tool clusters with their top-two mix, aggregate result, and duration', () => {
    const result = presentActivityEvents([{
      ...burst,
      count: 6,
      families: { command: 4, read: 2 },
      durationMs: 88_000,
    }], 'AGT-2088', null);

    expect(result[0]).toMatchObject({
      kind: 'system.status',
      category: 'activity-tool-summary',
      label: '6 Tool calls',
      explanation: 'shell ×4, read ×2 · all ok · 1m 28s',
      severity: 'info',
    });
  });

  it('keeps adjacent failed clusters separate and marks every aggregate red', () => {
    const failed = { ...burst, failures: 1, count: 3, families: { command: 2, read: 1 } };
    const result = presentActivityEvents([
      { ...failed, id: 'failed-1' },
      { ...failed, id: 'failed-2' },
    ], 'AGT-2088', null);

    expect(result).toHaveLength(2);
    expect(result.every((event) => event.kind === 'system.status' && event.severity === 'error')).toBe(true);
    expect(result.every((event) => event.kind === 'system.status' && event.explanation.includes('1 failed'))).toBe(true);
  });

  it('makes edit counts explicit and strips the worktree root while retaining full paths', () => {
    const fullPath = 'C:\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\frontend\\src\\app\\wiki\\marketing-studio.ts';
    const result = presentActivityEvents([{
      ...burst,
      count: 5,
      families: { edit: 5 },
      files: [fullPath, fullPath],
      durationMs: 12_000,
    }], 'AGT-2088', 'C:\\Projects\\Agent-Studio-Marketing', {
      worktreeRoot: 'C:\\Temp\\ass-worktrees\\Agent-Studio-Marketing',
      commitDiffRunIds: [2],
    });

    expect(result[0]).toMatchObject({
      kind: 'system.status',
      category: 'activity-edit-summary',
      label: '5 Edits · 1 file',
      explanation: 'frontend/src/app/wiki/marketing-studio.ts · all ok · 12s',
      activityPresentation: {
        kind: 'edit',
        fullPaths: [fullPath],
        relativePaths: ['frontend/src/app/wiki/marketing-studio.ts'],
        action: 'commit-diff',
      },
    });
  });

  it('recovers edit paths from the shared projector sample when files are absent', () => {
    const fullPath = 'C:/Temp/ass-worktrees/MKT-20/frontend/src/app/protocol.ts';
    const result = presentActivityEvents([{
      ...burst,
      count: 2,
      families: { edit: 2 },
      files: undefined,
      samples: { edit: `Edit ${fullPath}, Edit ${fullPath}` },
    }], 'MKT-20', null, {
      worktreeRoot: 'C:/Temp/ass-worktrees/MKT-20',
    });

    expect(result[0]).toMatchObject({
      label: '2 Edits · 1 file',
      explanation: 'frontend/src/app/protocol.ts · all ok · 0s',
      activityPresentation: {
        fullPaths: [fullPath],
        relativePaths: ['frontend/src/app/protocol.ts'],
      },
    });
  });
});

function wait(
  id: string,
  timestamp: string,
  state: SupervisorWaitEvent['state'],
  quietSeconds: number,
  line: number,
): SupervisorWaitEvent {
  return {
    id,
    kind: 'supervisor.wait',
    timestamp,
    runId: 2,
    rawRange: { source: 'AGT-2088', start: line, end: line },
    severity: state === 'killed' ? 'error' : state === 'quiet' ? 'warn' : 'info',
    state,
    quietSeconds,
    reason: `[watchdog] phase=TurnInProgress silence=${quietSeconds}s allowed=180/600s`,
  };
}
