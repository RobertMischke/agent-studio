import type { PostStepActivation } from '../../../../task-pipeline';

export type PipelinePanelDensity = 'compact' | 'comfortable';

export const PIPELINE_DENSITY_STORAGE_KEY = 'taskboard.overview.pipelineDensity';

export interface PipelineMetricRowLike {
  status: string;
  startedAt: string | null;
  durationMs: number;
  totalTokens: number;
  costKnown: boolean;
}

export interface PipelineMetricVisibility {
  time: boolean;
  duration: boolean;
  tokens: boolean;
  cost: boolean;
  any: boolean;
}

export interface PipelineGroupMetaRowLike {
  model: string | null;
  thinkingLevel: string | null;
  config: { activation?: PostStepActivation | null } | null;
}

export interface PipelineGroupActivationSummary {
  state: PostStepActivation['state'];
  source: PostStepActivation['source'];
  reason: string;
}

export function readPipelineDensity(storage: Pick<Storage, 'getItem'> | null = browserStorage()): PipelinePanelDensity {
  try {
    return storage?.getItem(PIPELINE_DENSITY_STORAGE_KEY) === 'comfortable' ? 'comfortable' : 'compact';
  } catch {
    return 'compact';
  }
}

export function writePipelineDensity(
  density: PipelinePanelDensity,
  storage: Pick<Storage, 'setItem'> | null = browserStorage(),
): void {
  try { storage?.setItem(PIPELINE_DENSITY_STORAGE_KEY, density); } catch { /* best-effort UI preference */ }
}

export function pipelineMetricVisibility(rows: readonly PipelineMetricRowLike[]): PipelineMetricVisibility {
  const time = rows.some(row => row.startedAt != null);
  const duration = rows.some(row => row.durationMs > 0 || (row.status === 'running' && row.startedAt != null));
  const tokens = rows.some(row => row.totalTokens > 0);
  const cost = rows.some(row => row.totalTokens > 0 && row.costKnown);
  return { time, duration, tokens, cost, any: time || duration || tokens || cost };
}

export function uniformGroupModel(rows: readonly PipelineGroupMetaRowLike[]): string | null {
  if (rows.length < 2) return null;
  const labels = rows.map(row => row.model
    ? `${row.model}${row.thinkingLevel ? ` · ${row.thinkingLevel}` : ''}`
    : null);
  return labels[0] != null && labels.every(label => label === labels[0]) ? labels[0] : null;
}

export function uniformGroupActivation(rows: readonly PipelineGroupMetaRowLike[]): PipelineGroupActivationSummary | null {
  if (rows.length < 2) return null;
  const values = rows.map(row => row.config?.activation ?? null);
  const first = values[0];
  return first != null && values.every(value => value?.state === first.state && value.source === first.source)
    ? first
    : null;
}

function browserStorage(): Storage | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage; } catch { return null; }
}
