import type { TaskInfo, ClientSummary, CliType } from '../../../../models/task.model';
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
  const ownerCli = isCliType(owner.defaultCliType) ? owner.defaultCliType : null;
  const ownerModel = owner.defaultModel ?? null;

  let icon: string;
  let label: string;
  let fullModel: string | null;
  let cliLbl: string | null;
  let source: EffectiveModelSource;
  let isDefault: boolean;

  if (execution?.status === 'running' && execution.model) {
    const cli = job.cliType ?? ownerCli;
    icon = cli ? cliTypeIcon(cli) : '\u{1F916}';
    label = shortModelName(execution.model);
    fullModel = execution.model;
    cliLbl = cli ? cliTypeLabel(cli) : null;
    source = 'run';
    isDefault = false;
  } else if (job.cliType || job.model) {
    const cli = job.cliType ?? ownerCli;
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

  const effectiveCli = job.cliType ?? ownerCli;
  const effectiveModel = source === 'run'
    ? job.execution?.model ?? job.model ?? ownerModel
    : job.model ?? ownerModel;

  lines.push(`<b>Model:</b> ${escapeHtml(effectiveModel ?? 'none')}${source === 'default' ? ' <i>(client default)</i>' : source === 'run' ? ' <i>(running)</i>' : ''}`);
  lines.push(`<b>CLI:</b> ${effectiveCli ? escapeHtml(cliTypeLabel(effectiveCli)) : 'none'}${!job.cliType && ownerCli ? ' <i>(client default)</i>' : ''}`);
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
