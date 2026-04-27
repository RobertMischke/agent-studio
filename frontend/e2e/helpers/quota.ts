import { api } from './api';

export interface CliUsageSection {
  cliType: 'copilot' | 'claude' | 'codex' | string;
  available: boolean;
  version: string | null;
  error: string | null;
  projects: Array<{ projectName: string; sessions: unknown[] }>;
}

export interface CliUsageReport {
  at: string;
  sections: CliUsageSection[];
}

export interface QuotaWindow {
  label: string;
  usedPct: number | null;
  resetLabel: string | null;
}

export interface QuotaSnapshot {
  cliType: string;
  plan: string | null;
  windows: QuotaWindow[];
  error: string | null;
}

export interface QuotaReport {
  at: string;
  snapshots: QuotaSnapshot[];
}

export const getCliUsage = () => api<CliUsageReport>('/api/cli/usage');
export const getQuotaReport = () => api<QuotaReport>('/api/cli/quota');

export interface QuotaSummary {
  cliType: string;
  available: boolean;
  hasHeadroom: boolean;
  worstUsedPct: number | null;
  raw: unknown;
}

/**
 * Quota check for a CLI. "Headroom" = the worst (highest used%) reported
 * window is below the safety threshold. The Claude probe lives in
 * `/api/cli/quota` and returns per-window usedPct.
 */
export async function getQuotaForCli(
  cliType: 'claude' | 'copilot' | 'codex',
  threshold = 95
): Promise<QuotaSummary> {
  const report = await getQuotaReport();
  const snap = report.snapshots.find(s => s.cliType === cliType);
  if (!snap) {
    return { cliType, available: false, hasHeadroom: false, worstUsedPct: null, raw: null };
  }
  if (snap.error) {
    return { cliType, available: false, hasHeadroom: false, worstUsedPct: null, raw: snap };
  }
  const used = snap.windows.map(w => w.usedPct).filter((n): n is number => typeof n === 'number');
  const worst = used.length ? Math.max(...used) : null;
  const hasHeadroom = worst === null ? true : worst < threshold;
  return { cliType, available: true, hasHeadroom, worstUsedPct: worst, raw: snap };
}

export const getClaudeQuota = () => getQuotaForCli('claude');
