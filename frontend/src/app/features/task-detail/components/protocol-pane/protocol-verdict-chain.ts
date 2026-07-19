import type { ReviewEvidenceEntry } from '../../../../models/task.model';
import type { ProtocolVerdict } from './protocol-verdict';

/**
 * The visible **verdict chain** (BEFUND 2): once a run is behind us the user
 * needs to see *which* signal is leading the head state and how it got there,
 * not just a single pill. The chain narrates the four decision points every
 * task passes through, each with its own status and a link to its evidence:
 *
 *   Run → Gate → Review aspects → Lane decision
 *
 * - **Run** — the run outcome parsed from `status.md` (the same signal the head
 *   verdict pill is derived from). When the accepted stand has overtaken a
 *   Blocked/Failed run, this step carries the *superseded* outcome so the
 *   sequence "run blocked → but accepted" is legible instead of contradictory.
 * - **Gate** — the deterministic runner-outcome gate (`TaskInfo.outcomeIssue`):
 *   watchdog timeout, permission block, missing sentinel, classifier miss. This
 *   is the automated evaluation layer between the raw run and human review.
 * - **Review aspects** — findings appended to `results/review-evidence.jsonl`
 *   (`TaskDetail.reviewEvidence`): code-review passes, task checks, audits.
 * - **Lane decision** — the leading state: `TaskInfo.orchestratorVerdict`
 *   (accept / reissue / escalate / pending) reconciled with the lane the card
 *   currently lives in. *This step leads the head verdict* (BEFUND 2 precedence).
 *
 * The chain also carries a one-line {@link VerdictChain.causalNarrative} that
 * links the earlier steps to the leading decision (BEFUND 3: "warum eskaliert
 * trotz 14 OK" — automated checks passed, but a high-severity review finding
 * sent the card to a human). Pure function so the protocol pane can wrap it in
 * a `computed()` and tests can hammer every branch.
 */

export type ChainStepStatus = 'ok' | 'problem' | 'unclear' | 'superseded' | 'neutral';

/** A click-through link from a chain step to the artefact that justifies it. */
export interface ChainEvidenceLink {
  label: string;
  /** Where the link points, so the host can route the click. */
  target: 'status' | 'review-evidence' | 'lane';
  /** Optional identifier for the target (review-evidence entry id, lane key). */
  ref?: string | null;
}

export type ChainStepKey = 'run' | 'gate' | 'review' | 'lane';

export interface VerdictChainStep {
  key: ChainStepKey;
  title: string;
  status: ChainStepStatus;
  summary: string;
  evidence: ChainEvidenceLink[];
}

export interface VerdictChain {
  steps: VerdictChainStep[];
  /**
   * BEFUND 3: one sentence linking the earlier steps to the leading decision,
   * so a user can see *why* the head state is what it is (e.g. why the card was
   * escalated even though the run's checks passed).
   */
  causalNarrative: string;
  /** Which step is the leading head state (BEFUND 2 precedence: usually the lane). */
  leadingStepKey: ChainStepKey;
}

export interface VerdictChainInputs {
  /** The already-reconciled head verdict (from {@link deriveProtocolVerdict}). */
  verdict: ProtocolVerdict;
  /** Canonical lane key the card lives in (`TaskInfo.state`). */
  laneState?: string | null;
  /** Latest orchestrator-review verdict (`TaskInfo.orchestratorVerdict`). */
  orchestratorVerdict?: 'pending' | 'reissue' | 'escalate' | 'accept' | null;
  /** Task-level review findings (`TaskDetail.reviewEvidence`). */
  reviewEvidence: ReviewEvidenceEntry[];
}

const ACCEPTED_LANE_STATES = new Set(['6-completed', '7-archive']);

/**
 * Build the four-step verdict chain from the same signals the head pill reads.
 * Returns `null` when there is nothing meaningful to narrate yet (no run and no
 * lane decision), so the caller can omit the chain entirely.
 */
export function deriveVerdictChain(input: VerdictChainInputs): VerdictChain | null {
  const run = deriveRunStep(input);
  const gate = deriveGateStep(input);
  const review = deriveReviewStep(input);
  const lane = deriveLaneStep(input);

  // Nothing to narrate: no run outcome yet and no lane decision.
  const hasSignal =
    run.status !== 'neutral' ||
    lane.status !== 'neutral' ||
    !!input.orchestratorVerdict;
  if (!hasSignal) return null;

  const leadingStepKey = leadingStep(input);
  const causalNarrative = buildCausalNarrative({ input, run, gate, review, lane });
  return { steps: [run, gate, review, lane], causalNarrative, leadingStepKey };
}

/**
 * Head-verdict labels that mean "there is no run outcome to narrate yet" — no
 * completed run has produced a classifiable outcome. The chain treats these as
 * a neutral Run step so it can be omitted entirely when nothing else leads.
 */
const NO_RUN_LABELS = new Set(['No run yet', 'Running']);

/** Run outcome — carries the superseded blocker when the stand overtook it. */
function deriveRunStep(input: VerdictChainInputs): VerdictChainStep {
  const { verdict } = input;
  const statusLink: ChainEvidenceLink = { label: 'status.md', target: 'status' };
  if (NO_RUN_LABELS.has(verdict.label)) {
    return {
      key: 'run',
      title: 'Run',
      status: 'neutral',
      summary: verdict.detail,
      evidence: [],
    };
  }
  if (verdict.superseded) {
    return {
      key: 'run',
      title: 'Run',
      status: 'superseded',
      summary: `${verdict.superseded.label}: ${verdict.superseded.detail}`,
      evidence: [statusLink],
    };
  }
  return {
    key: 'run',
    title: 'Run',
    status: verdict.kind,
    summary: `${verdict.label} — ${verdict.detail}`,
    evidence: [statusLink],
  };
}

/**
 * Gate — the deterministic runner-outcome classification. There is no separate
 * "gate" verdict on the wire; the runner-outcome issue *is* the automated gate
 * observability layer, so we surface it here. No issue means the gate did not
 * flag anything.
 */
function deriveGateStep(input: VerdictChainInputs): VerdictChainStep {
  const status = input.verdict; // reuse the head verdict for the summary-failed case
  if (status.label === 'Summary failed') {
    return {
      key: 'gate',
      title: 'Gate',
      status: 'problem',
      summary: 'Summary generation failed — the run could not be evaluated automatically.',
      evidence: [{ label: 'status.md', target: 'status' }],
    };
  }
  return {
    key: 'gate',
    title: 'Gate',
    status: 'ok',
    summary: 'No runner-gate issue flagged (watchdog, permissions, sentinel all clear).',
    evidence: [],
  };
}

/** Review aspects — findings from review-evidence.jsonl, ranked by severity. */
function deriveReviewStep(input: VerdictChainInputs): VerdictChainStep {
  const entries = input.reviewEvidence ?? [];
  const high = entries.filter((e) => e.severity === 'high');
  const warn = entries.filter((e) => e.severity === 'warn');

  if (entries.length === 0) {
    return {
      key: 'review',
      title: 'Review aspects',
      status: 'neutral',
      summary: 'No review findings recorded.',
      evidence: [],
    };
  }

  const evidence: ChainEvidenceLink[] = (high.length ? high : warn.length ? warn : entries)
    .slice(0, 4)
    .map((e) => ({ label: e.title, target: 'review-evidence' as const, ref: e.id }));

  if (high.length) {
    return {
      key: 'review',
      title: 'Review aspects',
      status: 'problem',
      summary: `${high.length} high-severity finding${high.length === 1 ? '' : 's'} of ${entries.length} total.`,
      evidence,
    };
  }
  if (warn.length) {
    return {
      key: 'review',
      title: 'Review aspects',
      status: 'unclear',
      summary: `${warn.length} warning${warn.length === 1 ? '' : 's'} of ${entries.length} finding${entries.length === 1 ? '' : 's'}, none high-severity.`,
      evidence,
    };
  }
  return {
    key: 'review',
    title: 'Review aspects',
    status: 'ok',
    summary: `${entries.length} finding${entries.length === 1 ? '' : 's'}, none blocking.`,
    evidence,
  };
}

/** Lane decision — the leading head state (BEFUND 2 precedence). */
function deriveLaneStep(input: VerdictChainInputs): VerdictChainStep {
  const laneLink: ChainEvidenceLink | null = input.laneState
    ? { label: input.laneState, target: 'lane', ref: input.laneState }
    : null;
  const evidence = laneLink ? [laneLink] : [];

  switch (input.orchestratorVerdict) {
    case 'accept':
      return { key: 'lane', title: 'Lane decision', status: 'ok', summary: 'Accepted by review — this is the leading stand.', evidence };
    case 'reissue':
      return { key: 'lane', title: 'Lane decision', status: 'unclear', summary: 'Sent back for rework (reissue).', evidence };
    case 'escalate':
      return { key: 'lane', title: 'Lane decision', status: 'problem', summary: 'Escalated to human review.', evidence };
    case 'pending':
      return { key: 'lane', title: 'Lane decision', status: 'unclear', summary: 'Awaiting a review decision.', evidence };
    default:
      break;
  }
  if (input.laneState && ACCEPTED_LANE_STATES.has(input.laneState)) {
    return { key: 'lane', title: 'Lane decision', status: 'ok', summary: `Card is in ${input.laneState} — an accepted stand.`, evidence };
  }
  if (input.laneState) {
    return { key: 'lane', title: 'Lane decision', status: 'neutral', summary: `Card is in ${input.laneState}; no review decision yet.`, evidence };
  }
  return { key: 'lane', title: 'Lane decision', status: 'neutral', summary: 'No lane decision yet.', evidence };
}

/**
 * The leading head state (BEFUND 2): the current lane / review decision leads
 * whenever one exists (an orchestrator verdict, or an accepted lane). Otherwise
 * the run outcome is all we have, so it leads.
 */
function leadingStep(input: VerdictChainInputs): ChainStepKey {
  if (input.orchestratorVerdict) return 'lane';
  if (input.laneState && ACCEPTED_LANE_STATES.has(input.laneState)) return 'lane';
  return 'run';
}

/**
 * BEFUND 3: connect the earlier steps to the leading decision in one sentence.
 * The escalate-with-clean-gate branch is the "warum eskaliert trotz 14 OK"
 * case: the automated gate passed, but a high-severity review finding is what
 * routed the card to a human.
 */
function buildCausalNarrative(args: {
  input: VerdictChainInputs;
  run: VerdictChainStep;
  gate: VerdictChainStep;
  review: VerdictChainStep;
  lane: VerdictChainStep;
}): string {
  const { input, run, gate, review } = args;
  const highCount = (input.reviewEvidence ?? []).filter((e) => e.severity === 'high').length;

  switch (input.orchestratorVerdict) {
    case 'escalate':
      if (gate.status === 'ok' && highCount > 0) {
        return `Automated checks passed, but ${highCount} high-severity review finding${highCount === 1 ? '' : 's'} escalated this to human review.`;
      }
      if (highCount > 0) {
        return `${highCount} high-severity review finding${highCount === 1 ? '' : 's'} escalated this to human review.`;
      }
      return 'Escalated to human review — the automated signals were not conclusive on their own.';
    case 'reissue':
      return 'The work was sent back for rework; the review aspects above list what to address.';
    case 'accept':
      if (run.status === 'superseded') {
        return `The run reported "${input.verdict.superseded?.label}", but review accepted the current stand — the blocker is earlier history, not the leading state.`;
      }
      return 'Run, gate, and review all cleared, so the card was accepted.';
    case 'pending':
      return 'The run is evaluated; a review decision on the leading state is still pending.';
    default:
      break;
  }

  if (run.status === 'superseded') {
    return `The run reported "${input.verdict.superseded?.label}"; it is kept as history because a later stand leads.`;
  }
  if (review.status === 'problem') {
    return `The run completed, but the review aspects flagged ${highCount} high-severity finding${highCount === 1 ? '' : 's'} to weigh before acceptance.`;
  }
  return 'Run and gate are the only signals so far; no review decision has been recorded yet.';
}
