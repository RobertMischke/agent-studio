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
import type { CliOutputLine, TaskInfo } from '../../../../models/task.model';
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
import type { TaskTimelineEvent } from '../../../task-timeline';

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
  /** True when a newer review artifact exists but has no fresh grade. */
  olderDelivery: boolean;
}

/** One automatic reissue and the timeline reason that caused it. */
export interface EscalationReissue {
  index: number;
  at: string;
  trigger: string;
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

/**
 * DtC step 6 — the escalation-category families that mean the ORCHESTRATOR (or
 * the infra under it) could not conclude and handed the task to a human: the
 * "GaveUpToHuman" terminal. These read distinctly from a logical NeedsReview,
 * where the agent itself concluded the work needs a human's judgement
 * (`[[TASK_BLOCKED]]` / `[[TASK_NEEDS_INPUT]]`) or a quality gate flagged it.
 *
 * The set mirrors the backend `HumanReviewEscalationCategories` give-up members
 * (infra crash / inconclusive / quota / environmental / cli-launch / watchdog /
 * pickup-zombie / empty-fast-exit / context-overflow / model-invalid /
 * quarantined / auto-failure-park). Source = the escalation category the runtime
 * already writes into the `status.md` stub and the orchestrator log — no new
 * side-channel.
 */
const GAVE_UP_CATEGORIES = new Set<string>([
  'infra-crash',
  'infra-crash-retries-exhausted',
  'orchestrator-inconclusive',
  'inconclusive-with-results',
  'quota-exhausted',
  'environmental',
  'environment-blocker',
  'permission-blocked',
  'cli-launch-failed',
  'auth-refresh-failed',
  'watchdog-kill',
  'pickup-zombie',
  'worktree-blocked',
  'empty-fast-exit',
  'context-overflow',
  'model-invalid',
  'quarantined',
  'auto-failure-park',
  'agent-git-violation',
  'human-decision-needed',
  'steer-unanswered',
]);

/** Presentable labels for the escalation categories the give-up banner shows. */
const CATEGORY_LABELS: Record<string, string> = {
  'infra-crash': 'Infra crash',
  'infra-crash-retries-exhausted': 'Infra crash retries exhausted',
  'orchestrator-inconclusive': 'Orchestrator inconclusive',
  'inconclusive-with-results': 'Inconclusive (partial results)',
  'quota-exhausted': 'Quota exhausted',
  environmental: 'Environmental fault',
  'environment-blocker': 'Environment blocker',
  'permission-blocked': 'Permission blocked',
  'cli-launch-failed': 'CLI launch failed',
  'auth-refresh-failed': 'Authentication refresh failed',
  'watchdog-kill': 'Watchdog kill',
  'pickup-zombie': 'Pickup zombie',
  'worktree-blocked': 'Worktree blocked',
  'empty-fast-exit': 'Empty fast exit',
  'context-overflow': 'Context overflow',
  'model-invalid': 'Model invalid',
  quarantined: 'Quarantined',
  'auto-failure-park': 'Auto-failure park',
  'agent-git-violation': 'Agent git violation',
  'human-decision-needed': 'Human decision needed',
  'steer-unanswered': 'Steer unanswered',
};

/** Turn a raw category slug into a human label (title-cases unknown slugs). */
export function escalationCategoryLabel(category: string): string {
  const key = category.trim().toLowerCase();
  if (CATEGORY_LABELS[key]) return CATEGORY_LABELS[key];
  return key
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

/**
 * `HumanReviewEscalation.BuildStatusStub` writes `- Category: <slug>` and
 * `- Reason: <text>` into the escalated card's `status.md` when a system
 * escalation left no agent-written summary (the exact infra-crash / inconclusive
 * give-up case). Lift those two lines back out so the panel can name the
 * category. Returns null when the status has no `Category:` line — i.e. the card
 * carries a real agent summary (a logical / quality escalation), which is
 * itself the signal that it is NOT a system give-up.
 */
export function parseStatusStubEscalation(
  statusMarkdown: string | null | undefined,
): { category: string; reason: string | null } | null {
  if (!statusMarkdown) return null;
  let category: string | null = null;
  let reason: string | null = null;
  for (const line of statusMarkdown.split(/\r?\n/)) {
    const cat = /^\s*-\s*Category:\s*(.+\S)\s*$/i.exec(line);
    if (cat && !category) category = cat[1].trim();
    const rea = /^\s*-\s*Reason:\s*(.+\S)\s*$/i.exec(line);
    if (rea && !reason) reason = rea[1].trim();
  }
  return category ? { category: category.toLowerCase(), reason } : null;
}

/**
 * Recover the latest system give-up from the existing orchestrator participant
 * in `logs/cli-output.log` (the same chat transcript the detail view already
 * polls). The runtime writes either the category as the leading typed tag or as
 * `(category: <slug>; run summary: ...)` on a `[giveup]` line. This parser only
 * accepts known system-escalation categories, so a logical completion-gate
 * decision cannot accidentally acquire the louder GaveUpToHuman treatment.
 *
 * This is a read-only projection over an existing source. It introduces no
 * persisted field and no endpoint beside the chat/output path already mounted
 * by the task detail.
 */
export function parseOrchestratorGiveUp(
  lines: readonly CliOutputLine[] | null | undefined,
): { category: string; reason: string | null } | null {
  if (!lines?.length) return null;

  for (let index = lines.length - 1; index >= 0; index--) {
    const line = lines[index];
    if (line.stream?.trim().toLowerCase() !== 'orchestrator') continue;

    const text = line.text?.trim();
    if (!text) continue;

    const categoryMatch = /\bcategory:\s*([a-z0-9][a-z0-9_-]*)/i.exec(text);
    const leadingTag = /^\s*\[([a-z0-9][a-z0-9_-]*)\]/i.exec(text);
    const category = (categoryMatch?.[1] ?? leadingTag?.[1] ?? '').toLowerCase();
    if (!GAVE_UP_CATEGORIES.has(category)) continue;

    const taggedBody = text.replace(/^\s*\[(?:giveup|[a-z0-9][a-z0-9_-]*)\]\s*/i, '').trim();
    const suffix = /\s*\(\s*category:\s*[a-z0-9][a-z0-9_-]*\s*(?:;\s*run summary:\s*([^)]*?))?\s*\)\s*\.?\s*$/i.exec(taggedBody);
    const body = (suffix ? taggedBody.slice(0, suffix.index) : taggedBody).trim().replace(/[.;:,]+$/, '');
    const runSummary = suffix?.[1]?.trim().replace(/[.;:,]+$/, '') || '';
    const reason = [body, runSummary ? `Run summary: ${runSummary}` : '']
      .filter(Boolean)
      .join('. ');

    return { category, reason: reason || null };
  }

  return null;
}

/**
 * Classification of an escalated card into the DtC terminal it reached:
 * `gave-up` (the orchestrator/infra could not conclude — GaveUpToHuman) vs
 * `needs-review` (a logical / quality escalation a human judges on its merits).
 * Only `gave-up` gets the distinct prominent banner; `needs-review` keeps the
 * standard escalation presentation.
 */
export interface EscalationClassView {
  kind: 'gave-up' | 'needs-review';
  /** Raw category slug (e.g. `infra-crash`), when one was recovered. */
  category: string | null;
  /** Human label for the category chip, when known. */
  categoryLabel: string | null;
  /** One-line honest reason, from the status stub or the steering event. */
  reason: string | null;
}

/**
 * Derive the escalation class from the escalation category the runtime already
 * recorded. Priority: the existing orchestrator chat/log line, then the legacy
 * `status.md` stub, then the steering event's `cause` / reason text (the
 * aspect-verdict infra path writes
 * `aspect-verdict-infra-crash`). A card with an escalate verdict but no
 * recognisable give-up category is a logical NeedsReview. Returns null when the
 * card is not an escalation at all.
 */
export function deriveEscalationClass(
  inputs: Pick<EscalationSummaryInputs, 'info' | 'statusMarkdown' | 'steering' | 'cliOutput'>,
): EscalationClassView | null {
  const isEscalation = inputs.info.orchestratorVerdict === 'escalate';
  const chatGiveUp = parseOrchestratorGiveUp(inputs.cliOutput);
  const stub = parseStatusStubEscalation(inputs.statusMarkdown);
  const cause = causeOf(inputs.steering);
  const steeringReason = inputs.steering?.reason?.trim() || null;

  // Category, best available: chat wins, then the legacy stub; otherwise sniff
  // the steering cause (e.g. `aspect-verdict-infra-crash`).
  let category = chatGiveUp?.category ?? stub?.category ?? null;
  if (!category && cause) {
    const hit = [...GAVE_UP_CATEGORIES].find((c) => cause.toLowerCase().includes(c));
    if (hit) category = hit;
  }

  const isGiveUp = !!category && GAVE_UP_CATEGORIES.has(category);
  if (!isGiveUp && !isEscalation) return null;

  return {
    kind: isGiveUp ? 'gave-up' : 'needs-review',
    category,
    categoryLabel: category ? escalationCategoryLabel(category) : null,
    reason: chatGiveUp?.reason ?? stub?.reason ?? steeringReason,
  };
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
  /**
   * DtC step 6 — whether the orchestrator/infra gave up (GaveUpToHuman) or a
   * human must review a logical/quality escalation. Drives the distinct
   * give-up banner. Null when the card is not an escalation.
   */
  escalation: EscalationClassView | null;
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
  /** Reissue history derived from quality-loop reopen rows. */
  reissues: EscalationReissue[];
  /** One reconciled operator-facing sentence for delivery and decision state. */
  stateSentence: string;
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
  /**
   * The card's `status.md` body (`TaskDetail.statusMarkdown`). Carries the
   * escalation category + reason for a system escalation (via the
   * `BuildStatusStub` `- Category:` / `- Reason:` lines); null when absent.
   */
  statusMarkdown: string | null;
  /** Existing task chat / orchestrator-log projection from `GET .../output`. */
  cliOutput?: readonly CliOutputLine[];
  /** Full chronological task ledger used for reissue provenance and budget. */
  timeline: readonly TaskTimelineEvent[];
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
    olderDelivery: chosen !== byNewest[0],
  };
}

/**
 * Lift every automatic reopen into a compact history row. Timeline details are
 * preferred because they carry the concrete gate cause; the human summary is
 * the fallback for older ledgers.
 */
export function deriveReissues(events: readonly TaskTimelineEvent[]): EscalationReissue[] {
  return events
    .filter((event) => event.kind === 'quality_loop_reopened')
    .map((event, index) => {
      const cause = event.details?.['cause']?.trim();
      const reason = event.details?.['reason']?.trim();
      const summary = event.summary?.trim();
      const trigger = cause && reason && !reason.toLowerCase().includes(cause.toLowerCase())
        ? `${cause}: ${reason}`
        : reason || cause || summary || 'Quality loop reopened the task.';
      return { index: index + 1, at: event.ts, trigger };
    });
}

/** Reconcile successful delivery signals with the still-acute human decision. */
export function buildEscalationStateSentence(
  delivery: EscalationDelivery,
  gateItems: readonly EscalationGateItem[],
  events: readonly TaskTimelineEvent[],
  reason: string | null,
): string {
  const merged = !!delivery.merge?.develop.merged || !!delivery.merge?.main.merged;
  const delivered = delivery.commitCount > 0;
  const deliveryText = merged
    ? 'Delivered and merged'
    : delivered
      ? 'Delivered but not merged'
      : 'Not delivered yet';
  const escalation = [...events].reverse().find((event) => event.kind === 'orchestrator_escalated');
  const attempt = Number(escalation?.details?.['attempt']);
  const maxAttempts = Number(escalation?.details?.['maxAttempts']);
  const budgetExhausted = Number.isFinite(attempt)
    && Number.isFinite(maxAttempts)
    && maxAttempts > 0
    && attempt >= maxAttempts;
  const why = budgetExhausted
    ? 'the reissue budget is exhausted'
    : reason?.trim() || 'the orchestrator escalated the remaining gaps';
  const open = gateItems.filter((item) => !item.checked).length;
  const gateText = open === 1 ? '1 gate point remains open' : `${open} gate points remain open`;
  return `${deliveryText}; waiting for your decision because ${why}, and ${gateText}.`;
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
  const delivery = buildDelivery(inputs.info);
  const reason = inputs.steering?.reason?.trim() || null;
  return {
    escalation: deriveEscalationClass(inputs),
    reason,
    cause: causeOf(inputs.steering),
    gateItems: items,
    gateSource: source,
    review: pickReviewHead(inputs.codeReviews),
    delivery,
    recommendation: deriveRecommendation(inputs.info.orchestratorVerdict),
    reissues: deriveReissues(inputs.timeline),
    stateSentence: buildEscalationStateSentence(delivery, items, inputs.timeline, reason),
  };
}

/** Pull the `Cause` context row out of the steering info, when present. */
function causeOf(steering: SteeringInfo | null): string | null {
  if (!steering) return null;
  const row = steering.context.find((c) => c.key.toLowerCase() === 'cause');
  return row?.value?.trim() || null;
}
