import type { QuotaSnapshot } from './quota.model';

/** Visible attribution for a failed refresh that retained last-good values. */
export function formatProbeFailureLabel(
  snapshot: Pick<QuotaSnapshot, 'cliType' | 'cliVersion' | 'probeFailedAt'>,
): string | null {
  if (!snapshot.probeFailedAt) return null;
  const failedAt = Date.parse(snapshot.probeFailedAt);
  const time = Number.isFinite(failedAt)
    ? new Date(failedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const rawVersion = snapshot.cliVersion ?? '';
  const version = rawVersion.match(/\d+(?:\.\d+){1,3}/)?.[0] ?? rawVersion.trim();
  return `probe failed ${time}, ${snapshot.cliType}${version ? ' ' + version : ''}`;
}
