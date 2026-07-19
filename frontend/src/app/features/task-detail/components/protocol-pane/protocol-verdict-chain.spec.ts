import { describe, expect, it } from 'vitest';
import { deriveVerdictChain, type VerdictChainInputs } from './protocol-verdict-chain';
import type { ProtocolVerdict } from './protocol-verdict';
import type { ReviewEvidenceEntry } from '../../../../models/task.model';

function verdict(overrides: Partial<ProtocolVerdict> = {}): ProtocolVerdict {
  return {
    kind: 'ok',
    emoji: '🟢',
    label: 'Success',
    detail: 'Last run completed successfully.',
    duration: null,
    superseded: null,
    ...overrides,
  };
}

function evidence(overrides: Partial<ReviewEvidenceEntry> = {}): ReviewEvidenceEntry {
  return {
    id: 'ev-1',
    source: 'code-review',
    severity: 'info',
    title: 'A finding',
    body: null,
    createdAt: '2026-07-09T00:00:00Z',
    runIndex: null,
    artifacts: [],
    fileRefs: [],
    acknowledged: false,
    followupJobId: null,
    ...overrides,
  };
}

function inputs(overrides: Partial<VerdictChainInputs> = {}): VerdictChainInputs {
  return {
    verdict: verdict(),
    laneState: null,
    orchestratorVerdict: null,
    reviewEvidence: [],
    ...overrides,
  };
}

describe('deriveVerdictChain', () => {
  it('returns null when there is no run outcome and no lane decision', () => {
    const chain = deriveVerdictChain(
      inputs({ verdict: verdict({ kind: 'unclear', label: 'No run yet', detail: 'Start the task.' }) }),
    );
    expect(chain).toBeNull();
  });

  it('always emits the four canonical steps in order', () => {
    const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'accept' }))!;
    expect(chain.steps.map((s) => s.key)).toEqual(['run', 'gate', 'review', 'lane']);
    expect(chain.steps.map((s) => s.title)).toEqual(['Run', 'Gate', 'Review aspects', 'Lane decision']);
  });

  describe('Run step', () => {
    it('mirrors the head verdict kind and links to status.md', () => {
      const chain = deriveVerdictChain(
        inputs({ verdict: verdict({ kind: 'problem', label: 'Blocked', detail: 'sandbox denied' }) }),
      )!;
      const run = chain.steps.find((s) => s.key === 'run')!;
      expect(run.status).toBe('problem');
      expect(run.summary).toContain('Blocked');
      expect(run.evidence).toEqual([{ label: 'status.md', target: 'status' }]);
    });

    it('carries the superseded blocker so the run→accept sequence stays legible', () => {
      const chain = deriveVerdictChain(
        inputs({
          verdict: verdict({
            kind: 'ok',
            label: 'Accepted',
            detail: 'Current stand: accepted by review.',
            superseded: { label: 'Blocked', detail: 'sandbox denied write to /etc' },
          }),
          orchestratorVerdict: 'accept',
        }),
      )!;
      const run = chain.steps.find((s) => s.key === 'run')!;
      expect(run.status).toBe('superseded');
      expect(run.summary).toContain('Blocked');
      expect(run.summary).toContain('sandbox denied write to /etc');
    });
  });

  describe('Gate step', () => {
    it('is ok when no summary/runner gate issue is present', () => {
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'accept' }))!;
      const gate = chain.steps.find((s) => s.key === 'gate')!;
      expect(gate.status).toBe('ok');
    });

    it('is a problem when summary generation itself failed', () => {
      const chain = deriveVerdictChain(
        inputs({ verdict: verdict({ kind: 'problem', label: 'Summary failed', detail: 'Haiku error' }) }),
      )!;
      const gate = chain.steps.find((s) => s.key === 'gate')!;
      expect(gate.status).toBe('problem');
      expect(gate.summary.toLowerCase()).toContain('summary generation failed');
    });
  });

  describe('Review aspects step', () => {
    it('is neutral with no findings', () => {
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'accept' }))!;
      const review = chain.steps.find((s) => s.key === 'review')!;
      expect(review.status).toBe('neutral');
      expect(review.summary).toContain('No review findings');
    });

    it('is a problem and links to each high-severity finding', () => {
      const chain = deriveVerdictChain(
        inputs({
          orchestratorVerdict: 'escalate',
          reviewEvidence: [
            evidence({ id: 'h1', severity: 'high', title: 'Race condition' }),
            evidence({ id: 'i1', severity: 'info', title: 'Nit' }),
          ],
        }),
      )!;
      const review = chain.steps.find((s) => s.key === 'review')!;
      expect(review.status).toBe('problem');
      expect(review.summary).toContain('1 high-severity finding');
      expect(review.evidence).toEqual([{ label: 'Race condition', target: 'review-evidence', ref: 'h1' }]);
    });

    it('is unclear when only warnings exist', () => {
      const chain = deriveVerdictChain(
        inputs({
          orchestratorVerdict: 'pending',
          reviewEvidence: [evidence({ id: 'w1', severity: 'warn', title: 'Style' })],
        }),
      )!;
      const review = chain.steps.find((s) => s.key === 'review')!;
      expect(review.status).toBe('unclear');
      expect(review.summary).toContain('1 warning');
    });

    it('is ok when findings exist but none are blocking', () => {
      const chain = deriveVerdictChain(
        inputs({
          orchestratorVerdict: 'accept',
          reviewEvidence: [evidence({ id: 'i1', severity: 'info' }), evidence({ id: 'i2', severity: 'info' })],
        }),
      )!;
      const review = chain.steps.find((s) => s.key === 'review')!;
      expect(review.status).toBe('ok');
      expect(review.summary).toContain('2 findings, none blocking');
    });

    it('caps the evidence links at four', () => {
      const many = Array.from({ length: 6 }, (_, i) => evidence({ id: `h${i}`, severity: 'high', title: `F${i}` }));
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'escalate', reviewEvidence: many }))!;
      const review = chain.steps.find((s) => s.key === 'review')!;
      expect(review.evidence).toHaveLength(4);
    });
  });

  describe('Lane decision step and precedence', () => {
    it('leads with the lane when an orchestrator verdict exists (BEFUND 2)', () => {
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'escalate', laneState: '4-auto-review' }))!;
      expect(chain.leadingStepKey).toBe('lane');
      const lane = chain.steps.find((s) => s.key === 'lane')!;
      expect(lane.status).toBe('problem');
      expect(lane.evidence).toEqual([{ label: '4-auto-review', target: 'lane', ref: '4-auto-review' }]);
    });

    it('leads with the lane when the card lives in an accepted lane', () => {
      const chain = deriveVerdictChain(inputs({ laneState: '6-completed' }))!;
      expect(chain.leadingStepKey).toBe('lane');
      expect(chain.steps.find((s) => s.key === 'lane')!.status).toBe('ok');
    });

    it('leads with the run when there is no lane decision', () => {
      const chain = deriveVerdictChain(
        inputs({ verdict: verdict({ kind: 'problem', label: 'Blocked', detail: 'x' }) }),
      )!;
      expect(chain.leadingStepKey).toBe('run');
    });

    it.each([
      ['accept', 'ok'],
      ['reissue', 'unclear'],
      ['escalate', 'problem'],
      ['pending', 'unclear'],
    ] as const)('maps orchestratorVerdict %s to lane status %s', (decision, status) => {
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: decision }))!;
      expect(chain.steps.find((s) => s.key === 'lane')!.status).toBe(status);
    });
  });

  describe('causal narrative (BEFUND 3)', () => {
    it('explains an escalation despite a clean gate as review-driven ("warum eskaliert trotz OK")', () => {
      const chain = deriveVerdictChain(
        inputs({
          verdict: verdict({ kind: 'ok', label: 'Success', detail: 'Last run completed successfully.' }),
          orchestratorVerdict: 'escalate',
          laneState: '4-auto-review',
          reviewEvidence: [evidence({ id: 'h1', severity: 'high', title: 'Data loss risk' })],
        }),
      )!;
      expect(chain.causalNarrative).toContain('Automated checks passed');
      expect(chain.causalNarrative).toContain('1 high-severity review finding');
      expect(chain.causalNarrative).toContain('human review');
    });

    it('explains an accepted stand that overtook a blocked run', () => {
      const chain = deriveVerdictChain(
        inputs({
          verdict: verdict({
            kind: 'ok',
            label: 'Accepted',
            detail: 'accepted by review',
            superseded: { label: 'Blocked', detail: 'sandbox denied' },
          }),
          orchestratorVerdict: 'accept',
        }),
      )!;
      expect(chain.causalNarrative).toContain('Blocked');
      expect(chain.causalNarrative).toContain('accepted the current stand');
      expect(chain.causalNarrative).toContain('earlier history');
    });

    it('explains a reissue as pointing at the review aspects', () => {
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'reissue' }))!;
      expect(chain.causalNarrative).toContain('sent back for rework');
    });

    it('explains a plain accept as all-clear', () => {
      const chain = deriveVerdictChain(inputs({ orchestratorVerdict: 'accept' }))!;
      expect(chain.causalNarrative).toContain('accepted');
    });
  });
});
