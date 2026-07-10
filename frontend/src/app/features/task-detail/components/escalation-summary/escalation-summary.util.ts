/**
 * Pure view-model builder for the escalation summary panel (AGT-2019).
 *
 * A `5e-escalated` card only showed the thin status protocol of the LAST run,
 * never WHY it escalated or what was already delivered (operator feedback
 * Robert 2026-07-09). This module aggregates the artifacts that already exist
 * for such a card into one decision-ready view — no new persistence, display
 * only:
 *
 *   1. ESCALATION REASON — the concrete open gate points, rendered as a
 *      checklist. Sourced, in priority order, from the reissue follow-up file
 *      checklist (`orchestrator-follow-up.md`, present only on reissued cards),
 *      the structured gate findings on the latest escalate/reissue steering
 *      timeline event (the common pure-escalation case), then the task-level
 *      review evidence.
 *   2. REVIEW VERDICT — the code-review grade + verdict + summary parsed from
 *      the newest `code-review-grade-*.md` frontmatter (already served by
 *      `GET …/code-review/list`).
 *   3. DELIVERY CONTEXT — is the work already in develop / main, and how many
 *      commits / files it carries (from `TaskInfo.mergeSignal` + `commits`).
 *   4. RECOMMENDATION — the gate's steer (accept as-is / reissue / needs
 *      decision), derived from `orchestratorVerdict` when present.
 *
 * Kept dependency-light and pure so the aggregation rules are unit-tested in
 * isolation from the Angular host, mirroring `pipeline-groups.util` /
 * `steering-detail.model`.
 */
import type { TaskInfo } from '../../../../models/task.model';
import type { ReviewEvidenceEntry, ReviewEvidenceSeverity } from '../../../../models/task.model';
import type { CodeReviewListEntry } from '../../../../services/task.service';
import {
  codeReviewVerdictTone,
  type CodeReviewVerdictTone,
} from '../code-review-verdict.util';
import {
  aspectVerdictTone,
  type AspectFinding,
  type AspectVerdictTone,
} from '../../../../components/aspect-findings';
import type { SteeringInfo } from '../../../../components/steering-detail';
import { buildMergeSignal, type MergeSignalView } from '../../../board';

/** One open gate point, rendered as a checklist row. */
export interface EscalationGateItem {
  /** Human text of the gate point. */
  text: string;
  /** Checklist state; `false` (unchecked) means still open. */
  checked: boolean;
  /** Optional per-aspect verdict token (e.g. `block`) for a tone chip. */
  verdict?: string | null;
  /** Central tone for the optional verdict chip. */
  tone?: AspectVerdictTone;
}

/** Where {@link EscalationSummaryView.gateItems} was sourced from. */
export type EscalationGateSource = 'follow-up' | 'gate-evidence' | 'review-evidence' | 'none';

/** Compact review-verdict head, from the newest code-review grade file. */
export interface EscalationReviewHead {
  /** Quality grade `A`/`B`/`C`/`D`, or null for older verdict-only reviews. */
  grade: string | null;
  /** Tone for the grade chip. */
  gradeTone: AspectVerdictTone;
  /** Raw verdict token (`pass`/`concerns`/`block`). */
  verdict: string;
  /** Tone for the verdict chip. */
  verdictTone: CodeReviewVerdictTone;
  /** One-line reviewer summary. */
  summary: string;
  /** Model that produced the grade, for provenance. */
  model: string | null;
  /** ISO instant the grade ran, for provenance. */
  runAt: string | null;
}

/** Delivery context: where the work landed + how much of it there is. */
export interface EscalationDelivery {
  /** Two-segment develop/main merge signal, or null when nothing is committed. */
  merge: MergeSignalView | null;
  /** Number of commits attributed to the task. */
  commitCount: number;
  /** Distinct changed files across those commits (0 when unknown). */
  filesChanged: number;
}

/** The three gate recommendations the operator chooses between. */
export type EscalationRecommendationKind = 'accept' | 'reissue' | 'needs-decision';

/** Recommendation line derived from the orchestrator verdict. */
export interface EscalationRecommendation {
  kind: EscalationRecommendationKind;
  /** Operator-facing label (e.g. "Needs decision"). */
  label: string;
  /** Tone driving the pill colour. */
  tone: 'ok' | 'warn' | 'danger';
}

/** The full aggregated view model the panel renders. */
export interface EscalationSummaryView {
  /** One-line escalation reason headline (from the steering event). */
  reason: string | null;
  /** Machine cause label (e.g. `completion-gate`), when recorded. */
  cause: string | null;
  /** The open gate points, as a checklist. */
  gateItems: EscalationGateItem[];
  /** Which artifact the gate items came from. */
  gateSource: EscalationGateSource;
  /** Compact review-verdict head, or null when no grade/verdict exists. */
  review: EscalationReviewHead | null;
  /** Delivery context (merge status + commit / file counts). */
  delivery: EscalationDelivery;
  /** Gate recommendation, or null when no verdict is recorded. */
  recommendation: EscalationRecommendation | null;
}

/** Inputs the host feeds in from the existing polled / fetched signals. */
export interface EscalationSummaryInputs {
  info: TaskInfo;
  reviewEvidence: readonly ReviewEvidenceEntry[];
  codeReviews: readonly CodeReviewListEntry[];
  /** Body of `orchestrator-follow-up.md`, or null when the file is absent. */
  followUpMarkdown: string | null;
  /** Latest escalate/reissue steering info from the timeline, or null. */
  steering: SteeringInfo | null;
}

/**
 * Matches a GitHub-flavoured task-list line: `- [ ] text` / `- [x] text`
 * (also `*`/`+` bullets, any indentation, upper or lower `x`). The follow-up
 * file writes exactly this shape for its open-item checklist.
 */
const CHECKLIST_LINE = /^\s*[-*+]\s+\[([ xX])\]\s+(.*\S)\s*$/;

/**
 * Parse the reissue follow-up markdown into gate items. Only genuine task-list
 * rows are lifted; the free-form preamble the orchestrator writes above the
 * list is ignored so the checklist stays clean. Returns [] when the input has
 * no task-list rows (or is absent), letting the caller fall back to the
 * timeline / evidence sources.
 */
export function parseFollowUpGateItems(markdown: string | null | undefined): EscalationGateItem[] {
  if (!markdown) return [];
  const out: EscalationGateItem[] = [];
  for (const line of markdown.split(/\r?\n/)) {
    const m = CHECKLIST_LINE.exec(line);
    if (!m) continue;
    const checked = m[1].toLowerCase() === 'x';
    const text = m[2].trim();
    if (!text) continue;
    out.push({ text, checked });
  }
  return out;
}

/**
 * Turn the structured aspect findings carried on an escalate/reissue steering
 * event into gate items. Each finding is an open point: label + verdict chip +
 * reason, rendered unchecked (they are the reasons the gate did NOT pass).
 */
export function gateItemsFromFindings(findings: readonly AspectFinding[]): EscalationGateItem[] {
  return findings.map((f) => {
    const reason = f.reason?.trim();
    const text = reason ? `${f.aspect}: ${reason}` : f.aspect;
    return {
      text,
      checked: false,
      verdict: f.verdict || null,
      tone: aspectVerdictTone(f.verdict),
    };
  });
}

/** Severity rank for evidence ordering (high first). */
const EVIDENCE_RANK: Record<ReviewEvidenceSeverity, number> = { high: 0, warn: 1, info: 2 };

/** Map an evidence severity to a verdict tone for its chip. */
function evidenceTone(severity: ReviewEvidenceSeverity): AspectVerdictTone {
  switch (severity) {
    case 'high':
      return 'danger';
    case 'warn':
      return 'warn';
    default:
      return 'neutral';
  }
}

/**
 * Last-resort gate items from the task-level review evidence: the unacknowledged
 * findings, high severity first. Acknowledged findings are dropped — the
 * operator has already dispositioned them, so they are no longer "open".
 */
export function gateItemsFromEvidence(entries: readonly ReviewEvidenceEntry[]): EscalationGateItem[] {
  return [...entries]
    .filter((e) => !e.acknowledged)
    .sort((a, b) => (EVIDENCE_RANK[a.severity] ?? 3) - (EVIDENCE_RANK[b.severity] ?? 3))
    .map((e) => ({
      text: e.title,
      checked: false,
      verdict: e.severity,
      tone: evidenceTone(e.severity),
    }));
}

/**
 * Choose the gate-item source in priority order and return the items plus a
 * label of where they came from. Follow-up checklist wins (it is the most
 * concrete "do these" list), then the structured steering findings, then the
 * review evidence.
 */
export function resolveGateItems(
  inputs: Pick<EscalationSummaryInputs, 'followUpMarkdown' | 'steering' | 'reviewEvidence'>,
): { items: EscalationGateItem[]; source: EscalationGateSource } {
  const followUp = parseFollowUpGateItems(inputs.followUpMarkdown);
  if (followUp.length > 0) return { items: followUp, source: 'follow-up' };

  const findings = gateItemsFromFindings(inputs.steering?.openItems ?? []);
  if (findings.length > 0) return { items: findings, source: 'gate-evidence' };

  const evidence = gateItemsFromEvidence(inputs.reviewEvidence);
  if (evidence.length > 0) return { items: evidence, source: 'review-evidence' };

  return { items: [], source: 'none' };
}

/** Map a code-review grade letter to a tone chip. A → ok … D → danger. */
export function gradeTone(grade: string | null | undefined): AspectVerdictTone {
  switch ((grade ?? '').trim().toUpperCase()) {
    case 'A':
      return 'ok';
    case 'B':
      return 'ok';
    case 'C':
      return 'warn';
    case 'D':
      return 'danger';
    default:
      return 'neutral';
  }
}

/**
 * Pick the review-verdict head from the code-review list. Prefers the newest
 * entry that carries a quality grade (the `code-review-grade-*.md` pass); falls
 * back to the newest entry with any verdict so a card reviewed only by the
 * older verdict path still shows a head. Null when the list is empty.
 *
 * The list endpoint already returns entries newest-first, but we do not rely on
 * that: we compare `runAt` explicitly so an unsorted caller is still correct.
 */
export function pickReviewHead(entries: readonly CodeReviewListEntry[]): EscalationReviewHead | null {
  if (entries.length === 0) return null;
  const byNewest = [...entries].sort((a, b) => (b.runAt ?? '').localeCompare(a.runAt ?? ''));
  const graded = byNewest.find((e) => !!e.grade?.trim());
  const chosen = graded ?? byNewest[0];
  if (!chosen) return null;
  const grade = chosen.grade?.trim() || null;
  return {
    grade,
    gradeTone: gradeTone(grade),
    verdict: chosen.verdict || 'unknown',
    verdictTone: codeReviewVerdictTone(chosen.verdict),
    summary: chosen.summary?.trim() || '',
    model: chosen.model?.trim() || null,
    runAt: chosen.runAt || null,
  };
}

/**
 * Build the delivery context: the develop/main merge signal plus commit and
 * distinct-file counts. Files are counted as the union of every commit's
 * `files` list so a file touched by two commits is not double-counted; when no
 * commit carries a file list we fall back to summing `filesChanged`.
 */
export function buildDelivery(info: TaskInfo): EscalationDelivery {
  const merge = buildMergeSignal(info);
  const commits = info.commits ?? (info.commit ? [info.commit] : []);
  const distinctFiles = new Set<string>();
  let filesChangedSum = 0;
  for (const c of commits) {
    filesChangedSum += c.filesChanged ?? 0;
    for (const f of c.files ?? []) distinctFiles.add(f);
  }
  const filesChanged = distinctFiles.size > 0 ? distinctFiles.size : filesChangedSum;
  return { merge, commitCount: commits.length, filesChanged };
}

/**
 * Derive the gate recommendation line from the orchestrator verdict. Escalated
 * cards carry `escalate` (the gate handed the call to a human → "Needs
 * decision"); a card that reached escalation after a reissue/accept verdict
 * maps to the corresponding steer. Null when no verdict is recorded.
 */
export function deriveRecommendation(
  verdict: TaskInfo['orchestratorVerdict'],
): EscalationRecommendation | null {
  switch (verdict) {
    case 'accept':
      return { kind: 'accept', label: 'Accept as-is', tone: 'ok' };
    case 'reissue':
      return { kind: 'reissue', label: 'Reissue', tone: 'warn' };
    case 'escalate':
      return { kind: 'needs-decision', label: 'Needs decision', tone: 'danger' };
    default:
      return null;
  }
}

/** Assemble the full escalation summary view model from the raw inputs. */
export function buildEscalationSummaryView(inputs: EscalationSummaryInputs): EscalationSummaryView {
  const { items, source } = resolveGateItems(inputs);
  return {
    reason: inputs.steering?.reason?.trim() || null,
    cause: causeOf(inputs.steering),
    gateItems: items,
    gateSource: source,
    review: pickReviewHead(inputs.codeReviews),
    delivery: buildDelivery(inputs.info),
    recommendation: deriveRecommendation(inputs.info.orchestratorVerdict),
  };
}

/** Pull the `Cause` context row out of the steering info, when present. */
function causeOf(steering: SteeringInfo | null): string | null {
  if (!steering) return null;
  const row = steering.context.find((c) => c.key.toLowerCase() === 'cause');
  return row?.value?.trim() || null;
}
