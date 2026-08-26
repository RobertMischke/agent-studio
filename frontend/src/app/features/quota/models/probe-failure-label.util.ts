import type { QuotaSnapshot } from './quota.model';

/** Compact, attributable marker for a failed probe over retained values. */
export function formatProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.error) return null;
  const failedAt = snapshot.probeFailedAt ? Date.parse(snapshot.probeFailedAt) : NaN;
  const time = Number.isFinite(failedAt)
    ? new Date(failedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : null;
  const version = snapshot.cliVersion
    ?.replace(/^codex-cli\s+/i, '')
    .replace(/^claude\s+code\s+/i, '')
    .trim();
  return `probe failed${time ? ` ${time}` : ''}, ${snapshot.cliType}${version ? ` ${version}` : ''}`;
}
