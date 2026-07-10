import { TaskState } from '../../../../models/task.model';
import type { TaskInfo, ClientSummary, CliType, TagRegistryEntry, EpicRollup, AutoLoopSnapshot, PendingIntent, TaskMode } from '../../../../models/task.model';
import type { TaskCommitInfo } from '../../../../features/git';
import type { StructuredTooltip } from 'coding-agent-chat/shared';
import type { MenuItem } from '../../../../components/menu';
import type { AutoReviewStatusView } from '../../../../services/auto-review-status.store';
import { cliTypeIcon, cliTypeLabel, shortModelName, taskModeIcon, taskModeLabel } from '../../../../services/format.util';
import { shouldShowFailureToast } from '../../../task-detail/services/run-outcome.util';

export interface TaskTypeChip {
  kind: string;
  label: string;
  icon: string;
  tooltip: string;
}

export interface TaskTokenBubble {
  label: string;
  total: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  model: string | null;
  lastUpdate: string | null;
  tier: 'neutral' | 'blue' | 'mauve' | 'peach';
  entries: { ts: string; tsLabel: string; model: string | null; total: number }[];
}

const FILE_LIST_MAX = 12;

export function buildTaskTypeChip(taskType: TaskInfo['taskType']): TaskTypeChip {
  const type = (taskType || 'chore').toLowerCase();
  if (type === 'bug') return { kind: 'bug', label: 'Bug', icon: 'warn', tooltip: 'Task type: Bug' };
  if (type === 'feature' || type === 'user-story') {
    return { kind: 'feature', label: 'Feature', icon: 'plus', tooltip: 'Task type: Feature' };
  }
  return { kind: 'chore', label: 'Chore', icon: 'dot', tooltip: 'Task type: Chore (default)' };
}

export interface ModeBadge {
  mode: TaskMode;
  label: string;
  icon: string;
  tooltip: string;
}

const MODE_TOOLTIP: Record<Exclude<TaskMode, 'coding'>, string> = {
  planning: 'Planning mode: read-only. The agent investigates and produces a plan without writing source.',
  research: 'Research mode: read-only with web access. The agent gathers information and reports findings.',
};

/**
 * Mode badge for the card. Only non-coding modes get a badge so that the board
 * stays quiet for the common case (coding is the default) while planning and
 * research cards are immediately recognizable. Glyphs come from `format.util`
 * so they match the create-dialog mode picker. Returns null for coding or when
 * the field is absent (older payloads).
 */
export function buildModeBadge(mode: TaskInfo['mode']): ModeBadge | null {
  if (mode !== 'planning' && mode !== 'research') return null;
  return {
    mode,
    label: taskModeLabel(mode),
    icon: taskModeIcon(mode),
    tooltip: MODE_TOOLTIP[mode],
  };
}

export function buildCommitTooltip(commit: TaskInfo['commit']): StructuredTooltip | string {
  if (!commit) return '';
  const subject = (commit.message || '').split('\n')[0];
  const files = commit.files ?? [];
  const title = `${commit.shortSha} - ${commit.filesChanged} file(s) changed`;
  const parts: string[] = [];
  if (subject) {
    parts.push(`<div>${escapeHtml(subject)}</div>`);
  }
  if (files.length > 0) {
    const shown = files.slice(0, FILE_LIST_MAX);
    const overflow = files.length - shown.length;
    const items = shown
      .map((file) => `<li><code>${escapeHtml(file)}</code></li>`)
      .join('');
    parts.push(`<ul>${items}</ul>`);
    if (overflow > 0) {
      parts.push(`<div><small>+${overflow} more file(s)</small></div>`);
    }
  }
  if (parts.length === 0) {
    return { title, body: `${commit.filesChanged} file(s) changed` };
  }
  return { title, body: parts.join('') };
}

/** Newest-first commit chain for a task. SSOT: prefer `commits[]`; fall back
 *  to the legacy singular `commit` only when `commits[]` is absent/empty, so
 *  older payloads still render. Never sources from repo HEAD. */
export function commitChainOf(job: TaskInfo): TaskCommitInfo[] {
  const chain = job.commits && job.commits.length > 0
    ? job.commits
    : (job.commit ? [job.commit] : []);
  // Stored oldest -> newest; the card shows the latest commit first.
  return [...chain].reverse();
}

export interface CommitRowView {
  shortSha: string;
  subject: string;
  filesChanged: number;
  hasFiles: boolean;
  tooltip: StructuredTooltip | string;
}

export interface CommitChainView {
  variant: 'full' | 'review';
  rows: CommitRowView[];
  moreCount: number;
  totalCount: number;
  anyFiles: boolean;
  moreTooltip: StructuredTooltip | string;
}

/** How many commit rows the card renders before collapsing to "+N more"
 *  (AC#4: 2-3 commits show in full; >3 show top-3 plus a disclosure). */
const COMMIT_ROWS_MAX = 3;

function commitSubject(commit: TaskCommitInfo): string {
  return (commit.message || '').split('\n')[0].trim();
}

export function buildCommitChainView(job: TaskInfo, variant: 'full' | 'review'): CommitChainView | null {
  const chain = commitChainOf(job);
  if (chain.length === 0) return null;
  const shown = chain.slice(0, COMMIT_ROWS_MAX);
  const rows: CommitRowView[] = shown.map((c) => ({
    shortSha: c.shortSha,
    subject: commitSubject(c),
    filesChanged: c.filesChanged,
    hasFiles: (c.files?.length ?? 0) > 0,
    tooltip: buildCommitTooltip(c),
  }));
  const rest = chain.slice(COMMIT_ROWS_MAX);
  return {
    variant,
    rows,
    moreCount: rest.length,
    totalCount: chain.length,
    anyFiles: rows.some((r) => r.hasFiles),
    moreTooltip: rest.length > 0 ? buildMoreCommitsTooltip(rest) : '',
  };
}

function buildMoreCommitsTooltip(rest: TaskCommitInfo[]): StructuredTooltip {
  const items = rest
    .map((c) => `<li><code>${escapeHtml(c.shortSha)}</code> ${escapeHtml(commitSubject(c))} <small>(${c.filesChanged} file(s))</small></li>`)
    .join('');
  return { title: `${rest.length} more commit(s)`, body: `<ul>${items}</ul>` };
}

// Lanes where the per-task commit chain is shown. Review lanes render the
// `review` variant; 3-progress shows the `full` variant that prefixes each row
// with the ⏺ glyph so the working agent can correlate it with its own
// auto-commit. Every other lane hides the chain entirely.
const COMMIT_PILL_LANES = new Set<string>([TaskState.Progress, TaskState.AutoReview, TaskState.HumanReview, TaskState.Escalated, '4-review']);
const COMMIT_REVIEW_LANES = new Set<string>([TaskState.AutoReview, TaskState.HumanReview, TaskState.Escalated, '4-review']);

/** Which commit-chain variant a lane renders, or null when the lane shows no
 *  chain. Keeps the lane->variant policy in one testable place instead of as
 *  component statics. */
export function commitChainVariant(state: string): 'full' | 'review' | null {
  if (!COMMIT_PILL_LANES.has(state)) return null;
  return COMMIT_REVIEW_LANES.has(state) ? 'review' : 'full';
}

/** Chain-aware commit tooltip. A single commit reuses the per-commit file
 *  list; a multi-commit chain lists every SHA with its subject and the rolled
 *  up file-change total. Empty chain -> no tooltip. Never sources repo HEAD. */
export function buildCommitChainTooltip(job: TaskInfo): StructuredTooltip | string {
  const chain = commitChainOf(job);
  if (chain.length === 0) return '';
  if (chain.length === 1) return buildCommitTooltip(chain[0]);
  const totalFiles = chain.reduce((sum, c) => sum + (c.filesChanged ?? 0), 0);
  const items = chain
    .map((c) => `<li><code>${escapeHtml(c.shortSha)}</code> ${escapeHtml(commitSubject(c))} <small>(${c.filesChanged} file(s))</small></li>`)
    .join('');
  return {
    title: `${chain.length} commits - ${totalFiles} file(s) changed`,
    body: `<ul>${items}</ul>`,
  };
}

export interface CommitEmptyBadge {
  tone: 'no-code' | 'discovery';
  label: string;
  tooltip: string;
}

/** Zero-commit diagnostic for review-lane cards (AC#3, bug (3)). Only fires in
 *  review lanes and only when the attributed chain is genuinely empty.
 *  `codeActivityDetected` (scanner signal, never repo HEAD) disambiguates the
 *  two cases the operator could not tell apart before:
 *   - `false` -> analysis-only task: a calm "no code changes" badge.
 *   - `true`  -> a run moved HEAD but no commit is attributed: an amber
 *     "commit discovery pending" diagnostic so a lost/undiscovered commit is
 *     visibly different from a correct no-op. */
export function buildCommitEmptyBadge(job: TaskInfo): CommitEmptyBadge | null {
  if (!COMMIT_REVIEW_LANES.has(job.state)) return null;
  if (commitChainOf(job).length > 0) return null;
  if (job.codeActivityDetected) {
    return {
      tone: 'discovery',
      label: 'commit discovery pending',
      tooltip: 'This task moved repository HEAD during a run, but no commit is attributed to it yet. Open the task and check the Git view: the attribution backfill may still be pending, or a commit landed that the rule could not associate. This is NOT an analysis-only task.',
    };
  }
  return {
    tone: 'no-code',
    label: 'no code changes',
    tooltip: 'No commit is attributed to this task and no run moved repository HEAD. This is an analysis-only / no-op task by design, not a lost commit.',
  };
}

export function buildTokenBubble(tokenSummary: TaskInfo['tokenSummary']): TaskTokenBubble | null {
  if (!tokenSummary) return null;
  const input = tokenSummary.inputTokens ?? 0;
  const output = tokenSummary.outputTokens ?? 0;
  const cacheRead = tokenSummary.cacheReadTokens ?? 0;
  const cacheWrite = tokenSummary.cacheCreationTokens ?? 0;
  const total = input + output + cacheRead + cacheWrite;
  if (total <= 0) return null;
  const tier = total >= 5_000_000 ? 'peach'
    : total >= 500_000 ? 'mauve'
      : total >= 50_000 ? 'blue'
        : 'neutral';
  const entries = (tokenSummary.entries ?? []).map((entry) => ({
    ts: entry.ts,
    tsLabel: formatShortTime(entry.ts),
    model: entry.model,
    total: (entry.inputTokens ?? 0) + (entry.outputTokens ?? 0) + (entry.cacheReadTokens ?? 0) + (entry.cacheCreationTokens ?? 0),
  }));
  return {
    label: formatTokens(total),
    total,
    input,
    output,
    cacheRead,
    cacheWrite,
    model: tokenSummary.lastModel ?? null,
    lastUpdate: tokenSummary.lastUpdate ? formatShortTime(tokenSummary.lastUpdate) : null,
    tier,
    entries,
  };
}

/** Compact tokens label: 850 -> "850", 2400 -> "2.4k", 850000 -> "850k", 3_100_000 -> "3.1M". */
export function formatTokens(n: number): string {
  if (!isFinite(n) || n <= 0) return '0';
  if (n < 1000) return Math.round(n).toString();
  if (n < 1_000_000) {
    const k = n / 1000;
    return (k >= 100 ? Math.round(k) : Number(k.toFixed(1))) + 'k';
  }
  const m = n / 1_000_000;
  return (m >= 100 ? Math.round(m) : Number(m.toFixed(1))) + 'M';
}

export function formatShortTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export type EffectiveModelSource = 'run' | 'explicit' | 'default' | 'human' | 'unknown';

export interface EffectiveModelChip {
  icon: string;
  label: string;
  fullModel: string | null;
  cliLabel: string | null;
  source: EffectiveModelSource;
  isDefault: boolean;
  tooltip: StructuredTooltip;
}

const CLI_TYPES_SET = new Set(['claude', 'codex', 'gemini']);

function isCliType(v: string | null | undefined): v is CliType {
  return !!v && CLI_TYPES_SET.has(v);
}

export function buildEffectiveModelChip(job: TaskInfo, owner: ClientSummary): EffectiveModelChip {
  const execution = job.execution;
  const jobCli = isCliType(job.cliType) ? job.cliType : null;
  const ownerCli = isCliType(owner.defaultCliType) ? owner.defaultCliType : null;
  const ownerModel = owner.defaultModel ?? null;

  let icon: string;
  let label: string;
  let fullModel: string | null;
  let cliLbl: string | null;
  let source: EffectiveModelSource;
  let isDefault: boolean;

  if (execution?.status === 'running' && execution.model) {
    const cli = jobCli ?? ownerCli;
    icon = cli ? cliTypeIcon(cli) : '\u{1F916}';
    label = shortModelName(execution.model);
    fullModel = execution.model;
    cliLbl = cli ? cliTypeLabel(cli) : null;
    source = 'run';
    isDefault = false;
  } else if (jobCli || job.model) {
    const cli = jobCli ?? ownerCli;
    icon = cli ? cliTypeIcon(cli) : '\u{1F916}';
    label = shortModelName(job.model ?? ownerModel);
    fullModel = job.model ?? ownerModel;
    cliLbl = cli ? cliTypeLabel(cli) : null;
    source = 'explicit';
    isDefault = false;
  } else if (ownerCli || ownerModel) {
    icon = ownerCli ? cliTypeIcon(ownerCli) : '\u{1F916}';
    label = shortModelName(ownerModel);
    fullModel = ownerModel;
    cliLbl = ownerCli ? cliTypeLabel(ownerCli) : null;
    source = 'default';
    isDefault = true;
  } else if (owner.kind === 'human') {
    icon = '\u{1F464}';
    label = 'human';
    fullModel = null;
    cliLbl = null;
    source = 'human';
    isDefault = false;
  } else {
    icon = '\u{1F916}';
    label = 'unknown';
    fullModel = null;
    cliLbl = null;
    source = 'unknown';
    isDefault = false;
  }

  const tooltip = buildModelTooltip(job, owner, source, ownerCli, ownerModel);

  return { icon, label, fullModel, cliLabel: cliLbl, source, isDefault, tooltip };
}

function buildModelTooltip(
  job: TaskInfo,
  owner: ClientSummary,
  source: EffectiveModelSource,
  ownerCli: CliType | null,
  ownerModel: string | null,
): StructuredTooltip {
  const lines: string[] = [];

  const jobCli = isCliType(job.cliType) ? job.cliType : null;
  const effectiveCli = jobCli ?? ownerCli;
  const effectiveModel = source === 'run'
    ? job.execution?.model ?? job.model ?? ownerModel
    : job.model ?? ownerModel;

  lines.push(`<b>Model:</b> ${escapeHtml(effectiveModel ?? 'none')}${source === 'default' ? ' <i>(client default)</i>' : source === 'run' ? ' <i>(running)</i>' : ''}`);
  lines.push(`<b>CLI:</b> ${effectiveCli ? escapeHtml(cliTypeLabel(effectiveCli)) : 'none'}${!jobCli && ownerCli ? ' <i>(client default)</i>' : ''}`);
  lines.push(`<b>Agent:</b> ${escapeHtml(job.agent || 'none')} <i>(pickup permission)</i>`);

  const ownerLabel = owner.displayName || owner.id;
  const defaultParts: string[] = [];
  if (ownerCli) defaultParts.push(cliTypeLabel(ownerCli));
  if (ownerModel) defaultParts.push(ownerModel);
  const defaultsStr = defaultParts.length > 0 ? defaultParts.join(' / ') : 'none';
  lines.push(`<b>Owner:</b> ${escapeHtml(ownerLabel)} (${escapeHtml(owner.id)})`);
  lines.push(`<b>Defaults:</b> ${escapeHtml(defaultsStr)}`);

  return {
    title: source === 'run' ? 'Running model' : source === 'default' ? 'Effective model (client default)' : 'Effective model',
    body: lines.join('<br>'),
  };
}

export interface TaskTagChip {
  id: string;
  label: string;
  color: string;
  ghost: boolean;
  concern: boolean;
  unparseable: boolean;
  tooltip: string;
}

const SUPPRESSED_CARD_TAG_TEXT = new Set([
  'ready',
  'reviewed',
  'reviewready',
  'readytosignoff',
  'autoreview',
  'autoreviewing',
  'humanreview',
  'concern',
  'concerns',
  'classifier',
  'classifierunknown',
  'classification',
  'orchestratormove',
  'orchestratormoved',
  'movedbyorchestrator',
  'qas',
  'qandas',
  'questionsandanswers',
]);

const LANE_MIRROR_CARD_TAG_TEXT: Record<string, readonly string[]> = {
  [TaskState.Backlog]: ['backlog'],
  [TaskState.Preparation]: ['preparation', 'prep'],
  [TaskState.OrchestratorPrep]: ['orchestratorprep', 'orchestratorpreparation', 'prep'],
  [TaskState.Ready]: ['ready'],
  [TaskState.Progress]: ['progress', 'inprogress'],
  [TaskState.FailedPickup]: ['failedpickup', 'pickupfailed'],
  [TaskState.AutoReview]: ['autoreview', 'review'],
  '4-review': ['review', 'autoreview'],
  [TaskState.HumanReview]: ['humanreview', 'review', 'reviewready', 'readytosignoff'],
  [TaskState.Escalated]: ['escalated', 'escalate', 'humanreview', 'decisionneeded'],
  [TaskState.Completed]: ['completed', 'complete', 'done'],
  [TaskState.Archive]: ['archive', 'archived'],
};

const REISSUE_AUTO_REVIEW_TAG_ID = 'reissue:autoreview';

function compactTagText(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/&/g, 'and')
    .replace(/[^a-z0-9]+/g, '');
}

function isSuppressedCardTag(id: string, entry: TagRegistryEntry | undefined, state: string | undefined): boolean {
  if (/^[a-z][a-z0-9-]*:(?:concerns?|unparseable)$/i.test(id)) return true;
  // The quality grade has its own prominent badge ({@link buildCodeReviewGradeBadge});
  // suppress the raw `code-review:grade-*` tag so it does not also render as a
  // dull chip in the tag row.
  if (CODE_REVIEW_GRADE_TAG_RE.test(id)) return true;
  const compactId = compactTagText(id);
  const compactLabel = entry ? compactTagText(entry.label) : '';
  if (SUPPRESSED_CARD_TAG_TEXT.has(compactId) || SUPPRESSED_CARD_TAG_TEXT.has(compactLabel)) return true;
  const laneMirrors = state ? LANE_MIRROR_CARD_TAG_TEXT[state] ?? [] : [];
  return laneMirrors.includes(compactId) || (compactLabel.length > 0 && laneMirrors.includes(compactLabel));
}

/**
 * Tag chips on the card. Looks up label + colour from the workspace registry
 * map; tags whose id no longer exists render as a faint "ghost" chip with the
 * raw id so the user knows to clean up.
 */
export function buildTagChips(
  ids: readonly string[] | undefined,
  byId: Map<string, TagRegistryEntry>,
  state?: string,
): TaskTagChip[] {
  const list = ids ?? [];
  if (list.length === 0) return [];
  return list.flatMap((id) => {
    const entry = byId.get(id);
    if (isSuppressedCardTag(id, entry, state)) return [];
    if (id.toLowerCase() === REISSUE_AUTO_REVIEW_TAG_ID) {
      return {
        id,
        label: 'Reissue',
        color: '#f59e0b',
        ghost: false,
        concern: false,
        unparseable: false,
        tooltip: 'Auto-review sent this task back for another attempt.'
      };
    }
    if (entry) {
      return {
        id,
        label: entry.label,
        color: entry.color,
        ghost: false,
        concern: false,
        unparseable: false,
        tooltip: entry.description ? `${entry.label}: ${entry.description}` : entry.label
      };
    }
    return {
      id,
      label: id,
      color: '#475569',
      ghost: true,
      concern: false,
      unparseable: false,
        tooltip: `Unknown tag '${id}'; registry entry was removed`
      };
  });
}

export interface OwnerChip {
  id: string;
  label: string;
  initials: string;
  emoji: string;
  background: string;
  border: string;
  foreground: string;
  tooltip: string;
}

function tintFromHex(hex: string, alpha: number): string {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return `rgba(100,116,139,${alpha})`;
  let body = m[1];
  if (body.length === 3) body = body.split('').map(ch => ch + ch).join('');
  const r = parseInt(body.slice(0, 2), 16);
  const g = parseInt(body.slice(2, 4), 16);
  const b = parseInt(body.slice(4, 6), 16);
  return `rgba(${r},${g},${b},${alpha})`;
}

function ownerInitials(label: string, id: string): string {
  const words = label
    .trim()
    .split(/[^\p{L}\p{N}]+/u)
    .filter(Boolean);
  const source = words.length >= 2
    ? `${words[0][0]}${words[1][0]}`
    : (words[0] ?? id).slice(0, 2);
  return (source || '??').toUpperCase();
}

/** Owner-attribution chip: compact user marker + the owner's chosen colour. */
export function buildOwnerChip(owner: ClientSummary): OwnerChip {
  const baseColour = owner.colour || '#64748b';
  const label = owner.displayName || owner.id;
  return {
    id: owner.id,
    label,
    initials: ownerInitials(label, owner.id),
    emoji: owner.emoji || '·',
    background: tintFromHex(baseColour, 0.12),
    border: tintFromHex(baseColour, 0.32),
    foreground: '#e2e8f0',
    tooltip: `Owner: ${label} (${owner.id})`
  };
}

/** Lane label derived from the state slug (`3-progress` -> `progress`). */
export function formatStateLabel(state: string): string {
  const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
  return name.replace(/-/g, ' ');
}

export type GitStateBadgeKind = 'pre-merge' | 'post-merge' | 'tagged';

export interface GitStateBadge {
  kind: GitStateBadgeKind;
  label: string;
  glyph: string;
  tooltip: string;
}

// Lane -> which cards may carry a git-state pill. Early lanes still stay quiet
// unless a real branch/tip is recorded; this lets prepared/ready worktree tasks
// show their branch before the first commit without inventing context for
// ordinary backlog cards. The lane no longer decides the LABEL: that comes from
// the provenance ground truth below. Completed/Archive still map straight
// through because the lane itself is the terminal fact (accepted into develop /
// archived).
const GIT_STATE_LANES: ReadonlySet<string> = new Set<string>([
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.OrchestratorPrep,
  TaskState.Ready,
  TaskState.Progress,
  TaskState.CodeNotComplete,
  TaskState.FailedPickup,
  TaskState.AutoReview,
  TaskState.HumanReview,
  TaskState.Escalated,
  TaskState.Completed,
  TaskState.Archive,
]);

const EARLY_GIT_CONTEXT_LANES: ReadonlySet<string> = new Set<string>([
  TaskState.Backlog,
  TaskState.Preparation,
  TaskState.OrchestratorPrep,
  TaskState.Ready,
  TaskState.FailedPickup,
]);

// Review lanes a PARALLEL run only reaches AFTER its task/<id> worktree was
// auto-integrated into develop and torn down (ADR-0052 / ASS-1731-1732). A card
// sitting here therefore has NO live worktree: its work already landed. A
// SEQUENTIAL run reaches the same lanes with no task branch at all, so we still
// gate the "landed" read on a branch having existed (a recorded branchTip).
const POST_INTEGRATION_REVIEW_LANES: ReadonlySet<string> = new Set<string>([
  TaskState.AutoReview,
  TaskState.HumanReview,
]);

function shortSha(sha: string): string {
  return sha.length > 7 ? sha.slice(0, 7) : sha;
}

/**
 * The CURRENT attempt's `task/<id>` tip: the newest recorded transition that
 * carries a non-null `branchTip`. Walking from the end makes a reissue point at
 * the live worktree, not an earlier run's tip. Null when no transition ever saw
 * a branch (a sequential run in the shared main checkout never cuts one).
 */
function currentBranchTip(prov: TaskInfo['provenance']): string | null {
  const transitions = prov?.transitions;
  if (!transitions?.length) return null;
  for (let i = transitions.length - 1; i >= 0; i--) {
    const tip = transitions[i]?.branchTip;
    if (tip) return tip;
  }
  return null;
}

/** Ground-truth "folded into develop" merge SHA, or null when not recorded. */
function recordedMergeSha(prov: TaskInfo['provenance']): string | null {
  const sha = prov?.merge?.mergeCommit;
  return sha && sha.trim().length > 0 ? sha : null;
}

/**
 * Git-state badge (ASS-1665, reworked for ASS-1752). Shows the operator *where
 * the work actually lives* from the provenance ground truth (ASS-1724), not a
 * lane guess. The lane only decides whether a pill shows at all; the label is
 * driven by three persisted facts on `job.provenance`:
 *
 *  1. Active worktree — a `task/<id>` branch exists (newest transition has a
 *     `branchTip`) and is not yet integrated. Names the branch + current-attempt
 *     tip, so a reissue tracks the live worktree.
 *  2. Landed in develop — the recorded merge fact, the terminal Completed lane,
 *     or a post-integration review lane whose parallel worktree was already torn
 *     down. Shows `develop @sha`; never a dead worktree path.
 *  3. Shared main checkout — a sequential run with no task branch at all. Says so
 *     instead of inventing a `task/<id>` that was never cut.
 *
 * Archived cards collapse to a quiet `tagged` pill. The three kinds keep the
 * existing pre-merge / post-merge / tagged styling.
 */
export function buildGitStateBadge(job: TaskInfo): GitStateBadge | null {
  if (!GIT_STATE_LANES.has(job.state)) return null;

  if (job.state === TaskState.Archive) {
    return {
      kind: 'tagged',
      label: 'tagged',
      glyph: '🏷',
      tooltip:
        "Git state: archived — this task is out of the active git flow; its work, if any, was integrated into develop before it was archived.",
    };
  }

  const prov = job.provenance ?? null;
  const branchName = prov?.branch || `task/${job.key || job.id}`;
  const tip = currentBranchTip(prov);
  const mergeSha = recordedMergeSha(prov);

  if (EARLY_GIT_CONTEXT_LANES.has(job.state) && !tip && !mergeSha) {
    return null;
  }

  // (2) Landed in develop. Ground-truth merge fact wins; otherwise the lane is
  // terminal (Completed) or a post-integration review lane whose parallel
  // worktree has already been torn down (a real branch was cut, so this is not a
  // sequential run). In every case the worktree, if any, is gone — show develop.
  const landed =
    !!mergeSha ||
    job.state === TaskState.Completed ||
    (POST_INTEGRATION_REVIEW_LANES.has(job.state) && !!tip);
  if (landed) {
    const label = mergeSha ? `develop @${shortSha(mergeSha)}` : 'develop';
    return {
      kind: 'post-merge',
      label,
      glyph: '⬇',
      tooltip: mergeSha
        ? `Git state: merged into develop at ${shortSha(mergeSha)}. The task/<id> worktree has been integrated and torn down — its work now lives on develop.`
        : "Git state: integrated — this task's commits live on the develop branch; its worktree, if any, has been torn down.",
    };
  }

  // (1) Active worktree run. A real task/<id> branch exists and is not yet
  // integrated. The tip is the CURRENT attempt's, so a reissue tracks the live
  // worktree rather than an earlier run.
  if (tip) {
    return {
      kind: 'pre-merge',
      label: branchName,
      glyph: '⎇',
      tooltip: `Git state: pre-merge — this task's work lives in its own ${branchName} worktree (current run, tip ${shortSha(tip)}) and is not yet integrated into develop.`,
    };
  }

  // (3) Sequential run in the shared main checkout: no task/<id> worktree was
  // ever cut. Say so instead of showing a branch that does not exist.
  return {
    kind: 'pre-merge',
    label: 'main checkout',
    glyph: '✎',
    tooltip:
      "Git state: pre-merge — this is a sequential run working directly in the shared main checkout; no isolated task/<id> worktree was created. Its work is not yet integrated into develop.",
  };
}

export type PipelineDotStatus = 'done' | 'active' | 'pending' | 'blocked';

export interface PipelineDot {
  id: 'pre' | 'run' | 'post' | 'review';
  label: string;
  status: PipelineDotStatus;
}

export interface PipelineDotsView {
  dots: PipelineDot[];
  currentLabel: string | null;
  tooltip: string;
}

function pipelineView(
  current: PipelineDot['id'] | null,
  blocked: PipelineDot['id'] | null,
  doneThrough: PipelineDot['id'] | null,
): PipelineDotsView {
  const order: PipelineDot['id'][] = ['pre', 'run', 'post', 'review'];
  const labels: Record<PipelineDot['id'], string> = {
    pre: 'Pre steps',
    run: 'Core agent work',
    post: 'Post steps',
    review: 'Review',
  };
  const doneIndex = doneThrough ? order.indexOf(doneThrough) : -1;
  const dots = order.map((id, index): PipelineDot => ({
    id,
    label: labels[id],
    status: blocked === id ? 'blocked'
      : current === id ? 'active'
        : index <= doneIndex ? 'done'
          : 'pending',
  }));
  const currentLabel = current ? labels[current] : null;
  return {
    dots,
    currentLabel,
    tooltip: `Pipeline: ${dots.map((dot) => `${dot.label} ${dot.status}`).join(', ')}`,
  };
}

/**
 * Tiny card-level pipeline indicator. The board payload intentionally does not
 * carry the full per-task pipeline execution; that lives behind the detail
 * endpoint. The card therefore maps the existing lane/phase/execution signals
 * to the four visible sections (Pre steps, core agent work, post steps, review) without inventing
 * per-step results.
 */
export function buildPipelineDots(job: TaskInfo): PipelineDotsView {
  switch (job.phase ?? null) {
    case 'intake-running':
      return pipelineView('pre', null, null);
    case 'intake-blocked':
      return pipelineView(null, 'pre', null);
    case 'intake-passed':
      return pipelineView(null, null, 'pre');
    case 'post-processing-running':
      return pipelineView('post', null, 'run');
    case 'post-processing-blocked':
      return pipelineView(null, 'post', 'run');
    case 'awaiting-review':
      return pipelineView(null, null, 'post');
  }

  if (job.state === TaskState.Progress) {
    if (job.execution?.status === 'running') {
      return pipelineView('run', null, 'pre');
    }
    return pipelineView(null, null, 'run');
  }

  if (job.state === TaskState.AutoReview || job.state === '4-review') {
    return pipelineView('review', null, 'post');
  }

  if (job.state === TaskState.HumanReview || job.state === TaskState.Escalated) {
    return pipelineView(null, null, 'review');
  }

  if (job.state === TaskState.Completed || job.state === TaskState.Archive) {
    return pipelineView(null, null, 'review');
  }

  return pipelineView(null, null, null);
}

export type PhaseBadgeTone =
  | 'human-ready'
  | 'intake-running'
  | 'intake-blocked'
  | 'intake-passed'
  | 'post-processing-running'
  | 'post-processing-blocked'
  | 'awaiting-review';
export interface PhaseBadge { label: string; tone: PhaseBadgeTone; tooltip: string; }

/**
 * Lifecycle-phase chip. Surfaces the `phase` substate on cards that carry one.
 * Returns null when the job has no explicit phase, so cards that predate the
 * field render exactly like before.
 */
export function buildPhaseBadge(phase: TaskInfo['phase']): PhaseBadge | null {
  switch (phase ?? null) {
    case 'human-ready':
      return null;
    case 'intake-running':
      return { label: 'Intake running', tone: 'intake-running',
               tooltip: 'Orchestrator intake is checking this card (separate runner from the coding CLI).' };
    case 'intake-blocked':
      return { label: 'Intake blocked', tone: 'intake-blocked',
               tooltip: 'Orchestrator intake flagged this card. Check the activity log for the reason and resolve before the coding runner can pick it up.' };
    case 'intake-passed':
      return { label: 'Intake passed', tone: 'intake-passed',
               tooltip: 'Orchestrator intake approved this card. The coding runner is now allowed to pick it up.' };
    case 'post-processing-running':
      return { label: 'Post processing', tone: 'post-processing-running',
               tooltip: 'The coding CLI has finished. An orchestrator or supporting agent is running post-processing before review.' };
    case 'post-processing-blocked':
      return { label: 'Post processing blocked', tone: 'post-processing-blocked',
               tooltip: 'Orchestrator post-processing needs a human decision or failed before it could pass this task to review.' };
    case 'awaiting-review':
      return { label: 'Awaiting review', tone: 'awaiting-review',
               tooltip: 'Post-processing finished and the task is waiting for the review transition.' };
    default:
      return null;
  }
}

export interface ExecutionBadge { label: string; tone: 'running' | 'failed' | 'cancelled'; }

export function buildExecutionBadge(job: TaskInfo): ExecutionBadge | null {
  const execution = job.execution;
  if (!execution) return null;

  // Lane wins over execution-status. The backend overlay already clears
  // Execution for non-progress tasks (TaskEndpointHelpers.WithRuntime), but a
  // stale poll snapshot or an optimistic move can briefly land on the card
  // before the next round-trip. Without this guard, a card in 4-auto-review /
  // 5-human-review can flash "Running live" while the task is not actually
  // executing in this lane.
  if (job.state !== TaskState.Progress) return null;

  if (execution.status === 'running') {
    return { label: 'Running live', tone: 'running' };
  }

  if (shouldShowFailureToast(execution)) {
    return { label: execution.exitCode === null ? 'Failed' : `Failed (${execution.exitCode})`, tone: 'failed' };
  }

  if (execution.runOutcome === 'noop') {
    return { label: 'NoOp', tone: 'cancelled' };
  }

  if (execution.runOutcome === 'blocked') {
    return { label: 'Blocked', tone: 'cancelled' };
  }

  if (execution.runOutcome === 'needs-input') {
    return { label: 'Needs input', tone: 'cancelled' };
  }

  // 'stopped' is the new deliberate-kill status from the backend (user pause,
  // Pause-&-Send, watchdog kill). Render as a calm "Stopped" pill, not a
  // failure. Legacy 'cancelled' value stays supported so older in-memory
  // CliExecution records keep rendering.
  if (execution.status === 'stopped' || execution.status === 'cancelled') {
    return { label: 'Stopped', tone: 'cancelled' };
  }

  return null;
}

export interface ReviewBadge { label: string; tone: 'generating' | 'ready' | 'failed'; tooltip: string; }

/**
 * Review-pill descriptor: shows the auto-review (Haiku summarizer) status on a
 * card that landed in 4-auto-review. Returns null when there is nothing to show
 * (no run, or the user already moved on).
 */
export function buildReviewBadge(summaryState: TaskInfo['summaryState']): ReviewBadge | null {
  if (!summaryState) return null;
  switch (summaryState.status) {
    case 'generating':
      return { label: 'summarizing', tone: 'generating',
               tooltip: 'Orchestrator is summarizing the run output (Haiku). The card will become quiet once status.md has been written.' };
    case 'ready':
      return null;
    case 'failed':
      return { label: 'review failed', tone: 'failed',
               tooltip: summaryState.errorMessage ?? 'Auto-review failed.' };
    default:
      return null;
  }
}

export interface AutoReviewProcessBadge { label: string; tone: 'active' | 'queued' | 'stale' | 'done'; tooltip: string; }

export function buildAutoReviewProcessBadge(job: TaskInfo, status: AutoReviewStatusView | null, nowMs: number): AutoReviewProcessBadge | null {
  if (job.state !== TaskState.AutoReview) return null;

  const matchesCurrent = !!status?.currentJob
    && status.currentJob === job.id
    && (!status.currentProject || status.currentProject === job.projectName);

  if (matchesCurrent) {
    return {
      label: 'reviewing now',
      tone: 'active',
      tooltip: 'Auto-review is currently running its multi-aspect pass for this task.'
    };
  }

  if (job.orchestratorVerdict) {
    return {
      label: `review ${job.orchestratorVerdict}`,
      tone: 'done',
      tooltip: `Auto-review has already recorded an orchestrator verdict: ${job.orchestratorVerdict}.`
    };
  }

  if (!status?.lastTickAt) {
    return null;
  }

  const ageMs = nowMs - Date.parse(status.lastTickAt);
  if (ageMs > 90_000) {
    return {
      label: 'review stale',
      tone: 'stale',
      tooltip: `Auto-review has not completed a tick since ${new Date(status.lastTickAt).toLocaleString()}.`
    };
  }

  return null;
}

// Lanes that sit in the "Done & Decide" super-column and carry an orchestrator
// verdict the operator must act on. 4-auto-review is deliberately excluded — it
// lives in the "active" column and already surfaces its verdict via the
// auto-review process badge.
const HUMAN_DECISION_LANES = new Set<string>([TaskState.HumanReview, TaskState.Escalated, '4-review']);

export interface HumanReviewBadge { label: string; tone: 'attention'; tooltip: string; }

/**
 * Human-decision badge. An escalated / reissue card parked in 5-human-review
 * used to render identically to a Completed card, hiding that a human still has
 * to act ("Failed-Cards sehen aus wie Done"). This pill makes the verdict
 * explicit: a loud red "Escalated" / "Needs rework" marker for action-required
 * verdicts. Accepted cards stay quiet; the lane and commit context carry enough
 * state without repeating "Reviewed" as another chip.
 */
export function buildHumanReviewBadge(job: TaskInfo): HumanReviewBadge | null {
  if (!HUMAN_DECISION_LANES.has(job.state)) return null;
  switch (job.orchestratorVerdict) {
    case 'escalate':
      return {
        label: 'Escalated',
        tone: 'attention',
        tooltip: 'Auto-review escalated this task: the orchestrator could not accept the result and a human must decide what happens next. This is NOT a completed task.'
      };
    case 'reissue':
      return {
        label: 'Needs rework',
        tone: 'attention',
        tooltip: 'Auto-review asked for a reissue: the work needs changes before it can be accepted. Waiting on a human to act.'
      };
    case 'accept':
      return null;
    default:
      return null;
  }
}

export interface ExternalDoneBadge { label: string; tooltip: string; }

/**
 * "extern erledigt" badge for a task completed out-of-band (operator chat,
 * external agent, remote host) and reconciled through the external-completion
 * endpoint. Renders next to the CLI/model chip so a card whose work happened
 * outside the runner reads as intentionally done, not abandoned. See
 * docs/concepts/out-of-band-task-completion.md §3.
 */
export function buildExternalDoneBadge(job: TaskInfo): ExternalDoneBadge | null {
  const ext = job.externalCompletion;
  if (!ext) return null;
  const source = (ext.source ?? '').trim() || 'external';
  const when = formatExternalCompletionDate(ext.completedAt);
  const summary = (ext.summary ?? '').trim();
  const parts = [`Completed out-of-band by ${source}${when ? ` on ${when}` : ''}.`];
  if (summary) parts.push(summary);
  parts.push('Reconciled via the external-completion endpoint; see results/deliverables.md.');
  return { label: 'extern erledigt', tooltip: parts.join('\n\n') };
}

function formatExternalCompletionDate(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString();
}

/** Host-level "this card needs a human" flag: an escalate/reissue verdict in a
 *  human-decision lane. Drives the red uniform ring + faint tint. */
export function cardNeedsAttention(job: TaskInfo): boolean {
  if (!HUMAN_DECISION_LANES.has(job.state)) return false;
  return job.orchestratorVerdict === 'escalate' || job.orchestratorVerdict === 'reissue';
}

/** Matches the per-task quality-grade tag the automatic code-review step hangs
 *  (`code-review:grade-a` .. `code-review:grade-d`). */
const CODE_REVIEW_GRADE_TAG_RE = /^code-review:grade-([abcd])$/i;

export type CodeReviewGradeLetter = 'A' | 'B' | 'C' | 'D';

export interface CodeReviewGradeBadge {
  grade: CodeReviewGradeLetter;
  /** Lower-case letter, drives the per-grade colour class on the badge. */
  tone: 'a' | 'b' | 'c' | 'd';
  tooltip: string;
}

const CODE_REVIEW_GRADE_MEANING: Record<CodeReviewGradeLetter, string> = {
  A: 'Solves the goal clearly — complete, coherent, and backed by tests / evidence.',
  B: 'Solid work that does the job, with small gaps a human can accept.',
  C: 'Concerns — the work is half-done or unclear; a reviewer would want changes before shipping.',
  D: 'Misses the goal, redundantly redoes existing behaviour, or leaves broken / half-finished work.',
};

/**
 * Quality-grade badge (ASS-1657). Every task that runs through the pipeline
 * gets an automatic A/B/C/D code-review grade, carried as a
 * `code-review:grade-{a-d}` tag. This lifts the grade out of the tag row into a
 * prominent, colour-coded card badge so the operator reads the quality verdict
 * at a glance. Returns null when no grade tag is present (older cards, or a task
 * that has not reached the grade step yet).
 */
export function buildCodeReviewGradeBadge(tags: readonly string[] | undefined): CodeReviewGradeBadge | null {
  for (const id of tags ?? []) {
    const m = CODE_REVIEW_GRADE_TAG_RE.exec(id);
    if (m) {
      const tone = m[1].toLowerCase() as 'a' | 'b' | 'c' | 'd';
      const grade = tone.toUpperCase() as CodeReviewGradeLetter;
      return {
        grade,
        tone,
        tooltip: `Quality grade ${grade} — automatic code-review step. ${CODE_REVIEW_GRADE_MEANING[grade]}`,
      };
    }
  }
  return null;
}

export interface OutcomeIssueBadge { label: string; tone: 'info' | 'warn' | 'high'; tooltip: string; }

export function buildOutcomeIssueBadge(issue: TaskInfo['outcomeIssue']): OutcomeIssueBadge | null {
  if (!issue) return null;
  const severity = (issue.severity ?? '').toLowerCase();
  const tone = severity === 'high' ? 'high' : severity === 'warn' ? 'warn' : 'info';
  const seen = issue.lastSeenAt ? `\nLast seen: ${formatShortTime(issue.lastSeenAt)}` : '';
  const summary = issue.summary ? `\n\n${issue.summary}` : '';
  return {
    label: issue.label || issue.kind,
    tone,
    tooltip: `Runner outcome issue: ${issue.kind}${seen}${summary}`
  };
}

/**
 * AGT-2029 waits-on dependency chip. Consumes the backend-computed
 * `waitsOn` status (fulfilled/open per target, blocked, cycle) so the card
 * shows what the task is waiting on, in which state, and can route to the
 * target - matching the scheduler's own decision (the runner uses the same
 * evaluation to gate auto-pickup). Null when the task has no dependencies.
 *
 * States: `open` (⏳, at least one dependency not yet complete - the card is
 * held back from auto-pickup), `ready` (✓, all complete - the card is
 * workable), `cycle` (⚠, a dependsOn cycle that can never be fulfilled -
 * a configuration error).
 */
export interface DependencyChip {
  glyph: string;
  label: string;
  tone: 'open' | 'ready' | 'cycle';
  tooltip: string;
  /** F33 key the chip navigates to on click (first open target, else the first). */
  targetKey: string | null;
  /** Direct nav target resolved by the backend (works across lanes/projects, incl. archive). */
  targetJobId: string | null;
  targetWatchPath: string | null;
}

export function buildDependencyChip(waitsOn: TaskInfo['waitsOn']): DependencyChip | null {
  if (!waitsOn || waitsOn.items.length === 0) return null;
  const items = waitsOn.items;
  const tooltip = dependencyTooltip(items, waitsOn.cycleDetected);

  if (waitsOn.cycleDetected) {
    const primary = items[0];
    return {
      glyph: '⚠',
      label: 'dep cycle',
      tone: 'cycle',
      tooltip,
      targetKey: primary?.key ?? null,
      targetJobId: primary?.targetJobId ?? null,
      targetWatchPath: primary?.targetWatchPath ?? null,
    };
  }

  const open = items.filter((i) => !i.fulfilled);
  if (open.length > 0) {
    const primary = open[0];
    const extra = open.length - 1;
    return {
      glyph: '⏳',
      label: `waits: ${primary.key}${extra > 0 ? ` +${extra}` : ''}`,
      tone: 'open',
      tooltip,
      targetKey: primary.key,
      targetJobId: primary.targetJobId ?? null,
      targetWatchPath: primary.targetWatchPath ?? null,
    };
  }

  const primary = items[0];
  const extra = items.length - 1;
  return {
    glyph: '✓',
    label: `${primary.key}${extra > 0 ? ` +${extra}` : ''}`,
    tone: 'ready',
    tooltip,
    targetKey: primary.key,
    targetJobId: primary.targetJobId ?? null,
    targetWatchPath: primary.targetWatchPath ?? null,
  };
}

/**
 * AGT-2029: resolve the task a dependency chip should open. Prefers the
 * backend-resolved target (jobId + watchPath — correct across projects and
 * lanes the board snapshot omits, e.g. an archived target), falling back to the
 * F33 key against the current board snapshot. Null when it is not loaded.
 */
export function resolveDependencyTarget(
  chip: DependencyChip,
  jobs: readonly TaskInfo[],
): TaskInfo | null {
  if (chip.targetJobId) {
    const byId = jobs.find(
      (t) => t.id === chip.targetJobId && (!chip.targetWatchPath || t.watchPath === chip.targetWatchPath),
    );
    if (byId) return byId;
  }
  if (chip.targetKey) {
    const upper = chip.targetKey.toUpperCase();
    const byKey = jobs.find((t) => (t.key ?? '').toUpperCase() === upper);
    if (byKey) return byKey;
  }
  return null;
}

function dependencyTooltip(
  items: NonNullable<TaskInfo['waitsOn']>['items'],
  cycle: boolean,
): string {
  const lines = items.map((i) => {
    const mark = i.fulfilled ? '✓' : '◦';
    const state = i.fulfilled ? 'done' : i.resolved ? 'open' : 'not created yet';
    const title = i.targetTitle ? ` — ${i.targetTitle.slice(0, 40)}` : '';
    return `${mark} ${i.key} (${state})${title}`;
  });
  const head = cycle
    ? 'Dependency cycle: this task can never be auto-picked until the chain is fixed via its references.'
    : items.every((i) => i.fulfilled)
      ? 'All dependencies complete — this task is workable.'
      : 'Waiting on these to reach completed/archive before pickup:';
  return `${head}\n${lines.join('\n')}`;
}

export function buildLoopTooltip(al: AutoLoopSnapshot): string {
  const tokenLine = `${al.tokensUsed.toLocaleString()} / ${al.maxTokens.toLocaleString()} orchestrator tokens`;
  const startedAt = (() => { try { return new Date(al.startedAt).toLocaleString(); } catch { return al.startedAt; } })();
  const lastQ = (al.lastQuestion ?? '').slice(0, 160);
  const lastErr = al.lastError ? `\nLast error: ${al.lastError}` : '';
  return `Auto-loop: orchestrator answering NEEDS_INPUT for this task.\n` +
         `Iteration ${al.iteration} of ${al.maxIterations}.\n` +
         `${tokenLine}.\nStarted ${startedAt}.${lastErr}\n\nLast question: ${lastQ}${(al.lastQuestion ?? '').length > 160 ? '...' : ''}`;
}

export function buildPendingTooltip(pi: PendingIntent): string {
  const when = (() => {
    try { return new Date(pi.savedAt).toLocaleString(); }
    catch { return pi.savedAt; }
  })();
  const preview = (pi.prompt ?? '').slice(0, 120);
  return `Pending follow-up (${pi.mode}) saved ${when}.\nWill run on next auto-pickup.\n\n${preview}${(pi.prompt ?? '').length > 120 ? '...' : ''}`;
}

// Context-menu action ids shared between the menu builder and the click
// handler in the component.
export const EPIC_ASSIGN_PREFIX = 'epic-assign:';
export const EPIC_DETACH_ID = 'epic-detach';
export const FILTER_DEPENDENTS_ID = 'filter-dependents';

/**
 * Right-click context-menu rows for a card: copy actions + (for non-epic cards)
 * the epic assign/detach submenu. The active epic is marked; a detach row is
 * appended only when the card is already attached to one.
 */
export function buildCardCtxMenuItems(
  job: TaskInfo,
  isEpic: boolean,
  epics: readonly EpicRollup[],
  currentEpicId: string | null,
): MenuItem[] {
  const items: MenuItem[] = [
    { kind: 'row', id: 'copy-name', label: 'Copy Name' },
    { kind: 'row', id: 'copy-id', label: 'Copy ID' },
  ];
  if (job.key) {
    items.push({ kind: 'row', id: 'copy-key', label: `Copy Key (${job.key})` });
    items.push({
      kind: 'row',
      id: FILTER_DEPENDENTS_ID,
      label: `Filter: tasks depending on ${job.key}`,
    });
  }

  // Epic assignment is only meaningful for ordinary task cards - an epic is not
  // a sub-task of another epic.
  if (!isEpic) {
    items.push({ kind: 'separator' });
    items.push({ kind: 'header', label: 'Epic' });
    if (epics.length === 0 && !currentEpicId) {
      items.push({ kind: 'row', id: 'epic-none', label: 'No epics in this project', disabled: true });
    } else {
      for (const epic of epics) {
        items.push({
          kind: 'row',
          id: EPIC_ASSIGN_PREFIX + epic.id,
          label: epic.title || epic.id,
          active: epic.id === currentEpicId,
        });
      }
      if (currentEpicId) {
        items.push({ kind: 'row', id: EPIC_DETACH_ID, label: 'Detach from epic' });
      }
    }
  }
  return items;
}
