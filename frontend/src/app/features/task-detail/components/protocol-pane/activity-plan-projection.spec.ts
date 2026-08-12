import { describe, expect, it } from 'vitest';
import type { CliOutputLine } from '../../../../models/task.model';
import type { TaskPlanView } from '../../../plan-strip/plan.model';
import type { ConversationEvent, PlanUpdateEvent } from 'coding-agent-chat/core';
import { withLivePlanSnapshot, withoutRawCodexTodoListFrames } from './activity-plan-projection';

describe('Activity live plan projection', () => {
  it('removes native TODO_LIST JSON only from the semantic stream', () => {
    const rawTodo = '{"type":"item.updated","item":{"type":"todo_list","items":[]}}';
    const lines: CliOutputLine[] = [
      { timestamp: '2026-08-11T10:00:00Z', stream: 'stdout', text: rawTodo },
      { timestamp: '2026-08-11T10:00:01Z', stream: 'stdout', text: 'Agent reply' },
    ];

    const semantic = withoutRawCodexTodoListFrames(lines);

    expect(semantic.map(line => line.text)).toEqual(['Agent reply']);
    expect(lines[0].text).toBe(rawTodo);
  });

  it('replaces all snapshots with one stable checklist event containing current statuses', () => {
    const snapshot = (id: string, timestamp: string, status: 'pending' | 'in_progress'): PlanUpdateEvent => ({
      id,
      kind: 'plan.update',
      timestamp,
      rawRange: { source: 'AGT-2641', start: 1, end: 1 },
      items: [{ id: 'inspect', title: 'Inspect Activity', status }],
    });
    const events: ConversationEvent[] = [
      snapshot('snapshot-1', '2026-08-11T10:00:00Z', 'in_progress'),
      snapshot('snapshot-2', '2026-08-11T10:00:01Z', 'pending'),
    ];
    const plan: TaskPlanView = {
      hasPlan: true,
      source: 'codex/todo_list',
      snapshotCount: 3,
      updatedAt: '2026-08-11T10:00:02Z',
      activeItemId: 'integrate',
      softEstimateMedian: null,
      items: [
        { id: 'inspect', title: 'Inspect Activity', status: 'done', subActionCount: 0, subActions: [] },
        { id: 'integrate', title: 'Integrate progress', status: 'active', subActionCount: 0, subActions: [] },
        { id: 'verify', title: 'Run tests', status: 'pending', subActionCount: 0, subActions: [] },
      ],
      unassignedSubActions: [],
    };

    const projected = withLivePlanSnapshot(events, plan, 'AGT-2641');
    const live = projected.filter((event): event is PlanUpdateEvent => event.kind === 'plan.update');

    expect(live).toHaveLength(1);
    expect(live[0].id).toBe('AGT-2641:live-plan');
    expect(live[0].items.map(item => item.status)).toEqual(['completed', 'in_progress', 'pending']);
  });
});
