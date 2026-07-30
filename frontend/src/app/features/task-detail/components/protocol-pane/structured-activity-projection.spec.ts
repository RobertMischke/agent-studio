import { describe, expect, it } from 'vitest';
import type { SystemStatusEvent, ToolBurstEvent } from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';
import { projectStructuredActivityContent } from './structured-activity-projection';

function fixture(): CliOutputLine[] {
  const at = (index: number) => `2026-07-28T10:40:${String(index).padStart(2, '0')}.000Z`;
  return [
    { timestamp: at(0), stream: 'system', text: "[runner] working tree ready on branch 'main'" },
    { timestamp: at(1), stream: 'system', text: '[runner] spawning codex exec -m gpt-5.6-sol -' },
    { timestamp: at(2), stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
    { timestamp: at(3), stream: 'stderr', text: 'user' },
    { timestamp: at(4), stream: 'stderr', text: 'Create the concept.' },
    { timestamp: at(5), stream: 'stderr', text: 'codex' },
    { timestamp: at(6), stream: 'stderr', text: 'I will inspect the result.' },
    { timestamp: at(7), stream: 'system', text: '[runner-log-delivery:fixture]' },
    { timestamp: at(8), stream: 'stderr', text: 'exec' },
    { timestamp: at(9), stream: 'stderr', text: '/bin/bash -lc "git diff -- docs/start/README.md"' },
    { timestamp: at(10), stream: 'stderr', text: ' succeeded in 18ms:' },
    { timestamp: at(11), stream: 'stderr', text: 'diff --git a/docs/start/README.md b/docs/start/README.md' },
    { timestamp: at(12), stream: 'stderr', text: '+{' },
    { timestamp: at(13), stream: 'stderr', text: '+  "title": "Apply Robert\'s selected Deck icon",' },
    { timestamp: at(14), stream: 'stderr', text: '+}' },
    { timestamp: at(15), stream: 'stderr', text: 'codex' },
    { timestamp: at(16), stream: 'stderr', text: 'Ready for review.' },
    { timestamp: at(17), stream: 'stderr', text: '[[TASK_DONE]]' },
    { timestamp: at(18), stream: 'system', text: '[runner] CLI exited 0; typedOutcome=ExplicitAgentDone' },
  ];
}

describe('projectStructuredActivityContent', () => {
  it('uses Codex record headers to project payloads as collapsible tool output', () => {
    const result = projectStructuredActivityContent(fixture(), 'AGT-2355');
    const tool = result.events.find((event): event is ToolBurstEvent => event.kind === 'toolBurst');

    expect(tool).toBeDefined();
    expect(tool?.families).toEqual({ command: 1 });
    expect(tool?.collapsedByDefault).toBe(true);
    expect(tool?.commands?.[0]).toMatchObject({
      command: '/bin/bash -lc "git diff -- docs/start/README.md"',
      status: 'completed',
      exitCode: 0,
    });
    expect(tool?.commands?.[0].output).toContain('diff --git a/docs/start/README.md');
    expect(tool?.commands?.[0].output).toContain('"title": "Apply Robert\'s selected Deck icon"');
    expect(result.events.filter((event) => event.kind === 'message.taskAgent'))
      .toEqual([
        expect.objectContaining({ body: 'I will inspect the result.' }),
        expect.objectContaining({ body: 'Ready for review.' }),
      ]);
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'result',
      label: 'Task complete',
    }));
    expect(result.projectionLines).toEqual([]);
  });

  it('projects runner system records quietly and drops delivery bookkeeping', () => {
    const result = projectStructuredActivityContent(fixture(), 'AGT-2355');
    const runner = result.events.filter((event): event is SystemStatusEvent =>
      event.kind === 'system.status' && event.category === 'runner');

    expect(runner).toHaveLength(3);
    expect(runner.map((event) => event.label)).toEqual([
      'Runner ready',
      'Runner started',
      'Runner finished',
    ]);
    expect(JSON.stringify(runner)).not.toContain('[runner]');
    expect(JSON.stringify(result.events)).not.toContain('[runner-log-delivery:');
  });

  it('drops an unowned leading fragment when a capped buffer starts inside a tool payload', () => {
    const tail = fixture().slice(10);
    const result = projectStructuredActivityContent(tail, 'AGT-2355');

    expect(JSON.stringify(result.events)).not.toContain('succeeded in 18ms');
    expect(JSON.stringify(result.events)).not.toContain('diff --git');
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'message.taskAgent',
      body: 'Ready for review.',
    }));
  });
});
