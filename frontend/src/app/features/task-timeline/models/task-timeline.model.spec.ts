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

describe('deriveCompletionLoop - operator reopen closes the loop', () => {
  const escalated = ev(TIMELINE_KIND.orchestratorEscalated, {
    ts: '2026-08-08T21:19:55Z',
    actor: 'system',
    summary: 'The remote agent reported a blocker: operator-release-missing',
    details: { category: 'agent-blocked', reason: 'The remote agent reported a blocker: operator-release-missing' },
  });

  it('an operator requeue after an escalation leaves no current verdict', () => {
    const s = deriveCompletionLoop([
      escalated,
      ev(TIMELINE_KIND.laneChanged, { ts: '2026-08-09T08:49:57Z', details: { from: '5e-escalated', to: '0-backlog' } }),
      ev(TIMELINE_KIND.operatorRequeued, { ts: '2026-08-09T08:49:57Z', actor: 'human:operator', summary: 'reopened' }),
      ev(TIMELINE_KIND.laneChanged, { ts: '2026-08-11T20:43:09Z', details: { from: '0-backlog', to: '2-ready' } }),
    ]);
    expect(s.hasActivity).toBe(false);
    expect(s.latestVerdict).toBeNull();
    expect(s.reason).toBeNull();
  });

  it('a lane change from a loop terminal back to a pre-run lane closes the loop as well', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.orchestratorVerdictAccepted, { summary: 'all aspects pass' }),
      ev(TIMELINE_KIND.laneChanged, { ts: '2026-08-09T08:49:57Z', details: { from: '5-human-review', to: '2-ready' } }),
    ]);
    expect(s.hasActivity).toBe(false);
    expect(s.latestVerdict).toBeNull();
  });

  it('a quality-loop reopen keeps its verdict through the lane change back to ready', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { details: { attempt: '2', maxAttempts: '3', gap: 'button misaligned' } }),
      ev(TIMELINE_KIND.laneChanged, { details: { from: '4-auto-review', to: '2-ready' } }),
      ev(TIMELINE_KIND.laneChanged, { details: { from: '2-ready', to: '3-progress' } }),
    ]);
    expect(s.latestVerdict).toBe('reopened');
    expect(s.reopenCount).toBe(1);
    expect(s.attempt).toBe(2);
  });

  it('a verdict after the operator reopen is current again and counts from zero', () => {
    const s = deriveCompletionLoop([
      ev(TIMELINE_KIND.qualityLoopReopened, { details: { attempt: '2', gap: 'old gap' } }),
      escalated,
      ev(TIMELINE_KIND.operatorRequeued, { ts: '2026-08-09T08:49:57Z' }),
      ev(TIMELINE_KIND.orchestratorVerdictAccepted, { ts: '2026-08-12T10:00:00Z', summary: 'done on the fresh attempt' }),
    ]);
    expect(s.latestVerdict).toBe('accepted');
    expect(s.reopenCount).toBe(0);
    expect(s.attempt).toBe(1);
    expect(s.at).toBe('2026-08-12T10:00:00Z');
  });
});
