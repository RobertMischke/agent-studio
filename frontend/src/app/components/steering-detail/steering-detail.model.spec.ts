import { describe, expect, it } from 'vitest';
import {
  isSteeringKind,
  steeringInfoFromEvent,
  steeringTone,
  steeringVerdictLabel,
} from './steering-detail.model';

describe('steering-detail.model', () => {
  it('maps each steering kind to its verdict + tone', () => {
    expect(steeringInfoFromEvent({ kind: 'orchestrator_verdict_accepted' })?.verdict).toBe('accept');
    expect(steeringInfoFromEvent({ kind: 'quality_loop_reopened' })?.verdict).toBe('reissue');
    expect(steeringInfoFromEvent({ kind: 'orchestrator_escalated' })?.verdict).toBe('escalate');
    expect(steeringInfoFromEvent({ kind: 'orchestrator_steered' })?.verdict).toBe('continuation');

    expect(steeringTone('accept')).toBe('ok');
    expect(steeringTone('reissue')).toBe('warn');
    expect(steeringTone('escalate')).toBe('danger');
    expect(steeringTone('continuation')).toBe('neutral');
  });

  it('returns null for a non-steering kind', () => {
    expect(steeringInfoFromEvent({ kind: 'agent_run_started' })).toBeNull();
    expect(isSteeringKind('agent_run_started')).toBe(false);
    expect(isSteeringKind('quality_loop_reopened')).toBe(true);
  });

  it('labels verdicts for the chip', () => {
    expect(steeringVerdictLabel('reissue')).toBe('Re-issue');
    expect(steeringVerdictLabel('escalate')).toBe('Escalate');
    expect(steeringVerdictLabel('accept')).toBe('Accept');
  });

  it('prefers gap then reason then summary for the headline', () => {
    expect(steeringInfoFromEvent({
      kind: 'quality_loop_reopened', summary: 's', details: { gap: 'g', reason: 'r' },
    })?.reason).toBe('g');
    expect(steeringInfoFromEvent({
      kind: 'orchestrator_escalated', summary: 's', details: { reason: 'r' },
    })?.reason).toBe('r');
    expect(steeringInfoFromEvent({
      kind: 'quality_loop_reopened', summary: 'just a summary',
    })?.reason).toBe('just a summary');
  });

  it('resolves open items from the structured findings JSON', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: {
        findings: JSON.stringify([
          { aspect: 'code-quality', verdict: 'block', reason: 'dup helper' },
        ]),
        gap: 'fallback blob',
      },
    });
    expect(info?.openItems).toEqual([
      { aspect: 'code-quality', verdict: 'block', reason: 'dup helper' },
    ]);
  });

  it('falls back to parsing the gap blob for open items', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: { gap: '- **requirement-fit** [concerns]: missing test' },
    });
    expect(info?.openItems).toEqual([
      { aspect: 'requirement-fit', verdict: 'concerns', reason: 'missing test' },
    ]);
  });

  it('collapses an aspect-dump blob to a terse headline (no raw blob in reason)', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: {
        gap:
          '- **requirement-fit** [concerns]: missing edge case\n' +
          '- **code-quality** [block]: zero net lines\n' +
          '- **documentation-impact** [concerns]: stale doc',
      },
    });
    // The per-aspect detail is carried by openItems; the headline must be a
    // single-liner and never repeat the raw `**`/`[]` markdown blob.
    expect(info?.openItems).toHaveLength(3);
    expect(info?.reason).toBe('multi-aspect-block: 3 aspects flagged');
    expect(info?.reason).not.toContain('**');
    expect(info?.reason).not.toContain('[');
  });

  it('headlines aspect dumps without a block as "N aspects flagged"', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: {
        gap:
          '- **requirement-fit** [concerns]: missing edge case\n' +
          '- **documentation-impact** [concerns]: stale doc',
      },
    });
    expect(info?.reason).toBe('2 aspects flagged');
  });

  it('keeps a plain short gap as the reason headline even with structured findings', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: {
        gap: 'diff churn without net change',
        findings: JSON.stringify([
          { aspect: 'code-quality', verdict: 'block', reason: 'zero net lines' },
        ]),
      },
    });
    // The gap is not an aspect dump, so it is a genuine short headline and is
    // preserved; the structured finding still populates openItems.
    expect(info?.reason).toBe('diff churn without net change');
    expect(info?.openItems).toHaveLength(1);
  });

  it('carries the verbatim steer prompt', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: { followUpPrompt: 'STEER THE DIFF, DO NOT RESTART' },
    });
    expect(info?.prompt).toBe('STEER THE DIFF, DO NOT RESTART');
  });

  it('builds context rows only for the keys present', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: {
        attempt: '2', maxAttempts: '3', cause: 'no-completion-signal',
        priorReissues: '1', resumeSessionId: 'sess-123',
      },
    });
    expect(info?.context).toEqual([
      { key: 'Attempt', value: '2 / 3' },
      { key: 'Prior re-issues', value: '1' },
      { key: 'Cause', value: 'no-completion-signal' },
      { key: 'Mode', value: 'resume' },
      { key: 'Session', value: 'sess-123' },
    ]);
  });

  it('infers fresh-run mode when no resume session is present', () => {
    const info = steeringInfoFromEvent({
      kind: 'quality_loop_reopened', details: { attempt: '2' },
    });
    expect(info?.context).toEqual([
      { key: 'Attempt', value: '2' },
      { key: 'Mode', value: 'fresh-run' },
    ]);
  });

  it('parses commits from a JSON array or a newline block', () => {
    expect(steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: { priorCommits: JSON.stringify(['a1b2c3 feat: x', 'd4e5f6 fix: y']) },
    })?.commits).toEqual(['a1b2c3 feat: x', 'd4e5f6 fix: y']);

    expect(steeringInfoFromEvent({
      kind: 'quality_loop_reopened',
      details: { priorCommits: '- a1b2c3 feat: x\n- d4e5f6 fix: y' },
    })?.commits).toEqual(['a1b2c3 feat: x', 'd4e5f6 fix: y']);
  });
});
