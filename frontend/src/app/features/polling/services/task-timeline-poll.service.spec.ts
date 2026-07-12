import { describe, expect, it } from 'vitest';
import { sanitizeTimelineEvents } from './task-timeline-poll.service';

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
