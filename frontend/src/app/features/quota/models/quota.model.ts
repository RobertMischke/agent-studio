import type { CliType } from '../../../models/task.model';

/**
 * Cycle 9 quota feature models. Lifted out of the kitchen-sink
 * `models/job.model.ts` per ADR-0034 + the architecture review. The
 * legacy file re-exports these so existing imports keep working;
 * new code should import from `features/quota/models/quota.model`
 * directly so the feature boundary stays visible.
 *
 * Subscription rate-limit reporting per CLI. Each CLI exposes one or
 * more "windows" - e.g. monthly premium requests for Copilot, 5h +
 * weekly buckets for Codex, rate-limit reset for Claude when over-
 * quota. `usedPct` above 100 means the user has overshot the
 * included allotment.
 */
export interface QuotaWindow {
  label: string;
  usedPct: number | null;
  used: number | null;
  limit: number | null;
  unit: string | null;
  resetAt: string | null;
  resetLabel: string | null;
}

export interface QuotaSnapshot {
  cliType: CliType;
  fetchedAt: string;
  plan: string | null;
  windows: QuotaWindow[];
  source: string | null;
  rawSample: string | null;
  error: string | null;
  /**
   * True when this snapshot is not yet trusted (AGT-2064): a single probe
   * showed an implausible downward jump no reset explains and a confirmation
   * re-probe has not agreed yet, or a live launch died with a usage-limit
   * error that contradicts these numbers. Older backends omit the field.
   */
  suspicious?: boolean;
  suspiciousReason?: string | null;
  /**
   * Version of the CLI the probe talked to ("codex-cli 0.149.0"). Lets the UI
   * attribute a failure to a specific CLI build. Older backends omit the field.
   */
  cliVersion?: string | null;
  /**
   * True when `windows` / `plan` are carried over from an earlier successful
   * probe because the latest probe failed (AGT-2679). The numbers are real but
   * were measured at `lastGoodAt`; `error` says why the refresh failed.
   *
   * Distinct from the locally computed TTL staleness in
   * `CliUsageQuotaRow.stale` - this one means "the probe itself failed".
   */
  stale?: boolean;
  /** When the carried-over windows were actually measured. */
  lastGoodAt?: string | null;
}

export interface QuotaReport {
  at: string;
  /**
   * Cache TTL in seconds. The UI computes the "stale" badge as
   * `now - snapshot.fetchedAt > ttlSeconds`. When the field is missing
   * (older backends), treat as 600.
   */
  ttlSeconds?: number;
  snapshots: QuotaSnapshot[];
}
