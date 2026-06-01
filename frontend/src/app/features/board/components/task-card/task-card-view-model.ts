import type { TaskInfo, ClientSummary, CliType } from '../../../../models/task.model';
import type { TaskCommitInfo } from '../../../../features/git';
import type { StructuredTooltip } from '../../../../components/tooltip';
import { cliTypeIcon, cliTypeLabel, shortModelName } from '../../../../services/format.util';

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
  if (type === 'bug') return { kind: 'bug', label: 'Bug', icon: '🐞', tooltip: 'Task type: Bug' };
  if (type === 'feature' || type === 'user-story') {
    return { kind: 'feature', label: 'Feature', icon: '✨', tooltip: 'Task type: Feature' };
  }
  return { kind: 'chore', label: 'Chore', icon: '·', tooltip: 'Task type: Chore (default)' };
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
const COMMIT_PILL_LANES = new Set(['3-progress', '4-auto-review', '5-human-review', '4-review']);
const COMMIT_REVIEW_LANES = new Set(['4-auto-review', '5-human-review', '4-review']);

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

const CLI_TYPES_SET = new Set(['copilot', 'claude', 'codex', 'gemini']);

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
