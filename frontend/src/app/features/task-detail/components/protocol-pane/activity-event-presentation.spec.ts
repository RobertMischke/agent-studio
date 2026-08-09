import { describe, expect, it } from 'vitest';
import {
  codexTextModeStderrTranscriptFragment,
  projectConversation,
  type ConversationEvent,
  type ToolBurstEvent,
} from 'coding-agent-chat/core';
import { sanitizeProjectionLines } from '../conversation-projection';
import {
  formatCompactTokens,
  presentActivityEvents,
  stripLegacyCompletionLines,
  type PresentedToolBurstEvent,
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

  it('projects edit files relative to the run worktree and retains absolute tooltip paths', () => {
    const root = 'C:\\Users\\operator\\AppData\\Local\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\MKT-20\\frontend';
    const first = 'C:\\Users\\operator\\AppData\\Local\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\MKT-20\\frontend\\src\\app\\campaign.ts';
    const second = 'C:\\Users\\operator\\AppData\\Local\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\MKT-20\\docs\\brief.md';
    const result = presentActivityEvents([{
      ...burst,
      count: 5,
      families: { edit: 5 },
      files: [first, first, second],
      samples: { edit: `Edit ${first}` },
    }], 'MKT-20', null, {
      worktreeRootsByRun: new Map([[2, root]]),
    });

    const presented = result[0] as PresentedToolBurstEvent;
    expect(presented.files).toEqual([
      'frontend/src/app/campaign.ts',
      'docs/brief.md',
    ]);
    expect(presented.fileDetails).toEqual([
      { displayPath: 'frontend/src/app/campaign.ts', fullPath: first.replace(/\\/g, '/') },
      { displayPath: 'docs/brief.md', fullPath: second.replace(/\\/g, '/') },
    ]);
    expect(presented.samples?.['edit']).toBe('Edit frontend/src/app/campaign.ts');
    expect(presented.rowPresentation).toEqual({
      primaryLabel: '5 Edits · 2 files',
      mixLabel: '',
      outcomeLabel: 'all ok',
      fileTooltip: `${first.replace(/\\/g, '/')}\n${second.replace(/\\/g, '/')}`,
    });
  });

  it('builds a top-two tool mix, success aggregate, and duration-ready row data', () => {
    const result = presentActivityEvents([{
      ...burst,
      count: 8,
      families: { search: 1, read: 2, command: 5 },
      failures: 0,
      durationMs: 88_000,
    }], 'AGT-2088', null);

    const presented = result[0] as PresentedToolBurstEvent;
    expect(presented.rowPresentation).toEqual({
      primaryLabel: '8 Tool calls',
      mixLabel: 'shell ×5, read ×2',
      outcomeLabel: 'all ok',
      fileTooltip: undefined,
    });
    expect(presented.durationMs).toBe(88_000);
  });

  it('keeps the failure aggregate explicit and preserves error severity', () => {
    const result = presentActivityEvents([{
      ...burst,
      count: 3,
      families: { command: 2, read: 1 },
      failures: 2,
      severity: 'error',
    }], 'AGT-2088', null);

    const presented = result[0] as PresentedToolBurstEvent;
    expect(presented.rowPresentation.outcomeLabel).toBe('2 failed');
    expect(presented.severity).toBe('error');
  });

  it('uses the matching worktree root for each run and leaves unrelated absolute paths intact', () => {
    const runOne = '/home/runner/cache/worktrees/AGT-1';
    const runTwo = '/home/runner/cache/worktrees/AGT-2';
    const events: ConversationEvent[] = [{
      ...burst,
      id: 'run-one',
      runId: 1,
      files: [`${runOne}/src/one.ts`],
    }, {
      ...burst,
      id: 'run-two',
      runId: 2,
      files: [`${runTwo}/src/two.ts`, '/opt/shared/generated.ts'],
    }];

    const result = presentActivityEvents(events, 'AGT-2', null, {
      worktreeRootsByRun: new Map([[1, runOne], [2, runTwo]]),
    }) as PresentedToolBurstEvent[];

    expect(result[0].files).toEqual(['src/one.ts']);
    expect(result[1].files).toEqual(['src/two.ts', '/opt/shared/generated.ts']);
  });

  it('resolves an already relative file to a full tooltip path', () => {
    const root = '/home/runner/cache/worktrees/AGT-2';
    const result = presentActivityEvents([{
      ...burst,
      files: ['./src/relative.ts'],
    }], 'AGT-2', null, {
      worktreeRootsByRun: new Map([[2, root]]),
    }) as PresentedToolBurstEvent[];

    expect(result[0].files).toEqual(['src/relative.ts']);
    expect(result[0].fileDetails).toEqual([{
      displayPath: 'src/relative.ts',
      fullPath: `${root}/src/relative.ts`,
    }]);
    expect(result[0].rowPresentation.fileTooltip).toBe(`${root}/src/relative.ts`);
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
});
