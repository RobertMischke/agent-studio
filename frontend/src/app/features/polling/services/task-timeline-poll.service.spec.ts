import { describe, expect, it } from 'vitest';
import {
  appendTimelineEvent,
  reconcileTimelineEvents,
  sanitizeTimelineEvents,
} from './task-timeline-poll.service';
import type { TaskTimelineEvent } from '../../task-timeline';

describe('sanitizeTimelineEvents', () => {
  it('strips ANSI from summaries and every steer detail field', () => {
    const [event] = sanitizeTimelineEvents([{
      ts: '2026-07-11T12:00:00Z',
      kind: 'orchestrator_escalated',
      actor: 'orchestrator',
      summary: '\u001b[33mVerification needs attention\u001b[0m',
      details: {
        reason: '[33m[39m Building...',
        plan: '\u001b[36mRun npm test\u001b[0m',
      },
    }]);

    expect(event.summary).toBe('Verification needs attention');
    expect(event.details).toEqual({
      reason: ' Building...',
      plan: 'Run npm test',
    });
  });
});

describe('timeline append reconciliation', () => {
  const event = (index: number): TaskTimelineEvent => ({
    ts: `2026-08-11T10:00:${String(index).padStart(2, '0')}Z`,
    kind: 'lane_changed',
    actor: 'system',
    runId: `run-${index}`,
    summary: `event ${index}`,
  });

  it('preserves the array and row identities for an unchanged poll', () => {
    const current = [event(1), event(2)];
    const result = reconcileTimelineEvents(current, current.map(item => ({ ...item })));
    expect(result).toBe(current);
    expect(result[0]).toBe(current[0]);
  });

  it('patches only appended poll rows while preserving the existing prefix', () => {
    const current = [event(1), event(2)];
    const appended = event(3);
    const result = reconcileTimelineEvents(current, [...current.map(item => ({ ...item })), appended]);
    expect(result).toEqual([...current, appended]);
    expect(result[0]).toBe(current[0]);
    expect(result[1]).toBe(current[1]);
  });

  it('deduplicates a pushed row that races the convergence poll', () => {
    const current = [event(1), event(2)];
    expect(appendTimelineEvent(current, { ...current[1] })).toBe(current);
    expect(appendTimelineEvent(current, event(3))).toHaveLength(3);
  });
});
