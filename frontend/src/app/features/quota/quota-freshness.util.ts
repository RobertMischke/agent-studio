import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.isStale || snapshot.probeFailedAt) return true;
  const fetchedMs = snapshot.fetchedAt ? Date.parse(snapshot.fetchedAt) : NaN;
  return !Number.isFinite(fetchedMs) || Math.max(0, now - fetchedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const failedMs = Date.parse(snapshot.probeFailedAt);
  const time = Number.isFinite(failedMs)
    ? new Date(failedMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const cli = cliLabel(snapshot.cliType);
  const version = normalizedVersion(snapshot.failedProbeCliVersion ?? snapshot.cliVersion);
  return `Stale since ${time} · probe failed · ${cli}${version ? ` ${version}` : ''}`;
}

/** Calm detail copy that never repeats process cancellation implementation text. */
export function quotaProbeFailureDetail(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  return 'The latest quota probe did not complete. Showing the last-good local reading.';
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}

function cliLabel(cliType: string): string {
  switch (cliType.toLowerCase()) {
    case 'claude': return 'Claude';
    case 'codex': return 'Codex';
    default: return cliType;
  }
}
