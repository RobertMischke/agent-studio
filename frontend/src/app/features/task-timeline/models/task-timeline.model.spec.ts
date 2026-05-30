import { describe, expect, it } from 'vitest';
import {
  TIMELINE_KIND,
  deriveCompletionLoop,
  type TaskTimelineEvent,
} from './task-timeline.model';

function ev(kind: string, overrides: Partial<TaskTimelineEvent> = {}): TaskTimelineEvent {
  return {
    ts: '2026-05-30T10:00:00Z',
    kind,
    actor: 'system',
    summary: '',
    ...overrides,
  };
}

describe('deriveCompletionLoop', () => {
  it('returns the empty state for no events', () => {
    const s = deriveCompletionLoop([]);
    expect(s.hasActivity).toBe(false);
    expect(s.latestVerdict).toBeNull();
    expect(s.reopenCount).toBe(0);
    expect(s.attempt).toBeNull();
  });

  it('ignores non-verdict events (run/pipeline lifecycle only)', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.promptCreated),
      ev(TIMELINE_KIND.agentRunStarted),
      ev(TIMELINE_KIND.agentRunFinished, { summary: 'claimed done' }),
      ev(TIMELINE_KIND.postStepFinished),
    ]);
    expect(s.hasActivity).toBe(false);
    expect(s.latestVerdict).toBeNull();
  });

  it('counts reopens and takes the latest verdict as current state', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.agentRunFinished, { summary: 'claimed done' }),
      ev(TIMELINE_KIND.qualityLoopReopened, {
        ts: '2026-05-30T10:05:00Z',
        actor: 'quality-loop',
        summary: 'reopened',
        details: { attempt: '2', maxAttempts: '3', gap: 'button misaligned' },
      }),
    ]);
    expect(s.hasActivity).toBe(true);
    expect(s.latestVerdict).toBe('reopened');
    expect(s.reopenCount).toBe(1);
    expect(s.attempt).toBe(2);
    expect(s.maxAttempts).toBe(3);
    expect(s.reason).toBe('button misaligned');
    expect(s.at).toBe('2026-05-30T10:05:00Z');
  });

  it('tracks multiple reopens and reports the count', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { details: { attempt: '2', maxAttempts: '5', gap: 'first gap' } }),
      ev(TIMELINE_KIND.qualityLoopReopened, { ts: '2026-05-30T11:00:00Z', details: { attempt: '3', maxAttempts: '5', gap: 'second gap' } }),
    ]);
    expect(s.reopenCount).toBe(2);
    expect(s.attempt).toBe(3);
    expect(s.reason).toBe('second gap');
  });

  it('escalation: reads reason + attempt from details', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { details: { attempt: '2', maxAttempts: '3', gap: 'g' } }),
      ev(TIMELINE_KIND.orchestratorEscalated, {
        ts: '2026-05-30T12:00:00Z',
        actor: 'orchestrator',
        summary: 'handed to human',
        details: { attempt: '3', maxAttempts: '3', reason: 'attempt budget exhausted' },
      }),
    ]);
    expect(s.latestVerdict).toBe('escalated');
    expect(s.attempt).toBe(3);
    expect(s.maxAttempts).toBe(3);
    expect(s.reason).toBe('attempt budget exhausted');
  });

  it('accepted terminal: falls back to reopenCount + 1 when no attempt counter', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { details: { attempt: '2', maxAttempts: '3', gap: 'g' } }),
      ev(TIMELINE_KIND.orchestratorVerdictAccepted, {
        ts: '2026-05-30T13:00:00Z',
        actor: 'orchestrator',
        summary: 'all aspects pass',
      }),
    ]);
    expect(s.latestVerdict).toBe('accepted');
    // one reopen => attempt 2 (initial run + one reopen).
    expect(s.attempt).toBe(2);
    // accepted summary used as reason when no gap/reason detail.
    expect(s.reason).toBe('all aspects pass');
  });

  it('accepted with no reopens reports attempt 1', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.orchestratorVerdictAccepted, { actor: 'orchestrator', summary: 'done first try' }),
    ]);
    expect(s.latestVerdict).toBe('accepted');
    expect(s.attempt).toBe(1);
    expect(s.reopenCount).toBe(0);
  });

  it('prefers gap over reason over summary for the reason field', () => {
    const gapWins = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { summary: 's', details: { gap: 'the gap', reason: 'the reason' } }),
    ]);
    expect(gapWins.reason).toBe('the gap');

    const summaryFallback = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { summary: 'just the summary' }),
    ]);
    expect(summaryFallback.reason).toBe('just the summary');
  });
});
