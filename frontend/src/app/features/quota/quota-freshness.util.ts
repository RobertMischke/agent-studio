import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.isStale) return true;
  if (snapshot.probeFailedAt) return true;
  const capturedAt = snapshot.capturedAt ?? snapshot.fetchedAt;
  const fetchedMs = capturedAt ? Date.parse(capturedAt) : NaN;
  return !Number.isFinite(fetchedMs) || Math.max(0, now - fetchedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const failedMs = Date.parse(snapshot.probeFailedAt);
  const time = Number.isFinite(failedMs)
    ? new Date(failedMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const cli = snapshot.cliType.toLowerCase();
  const version = normalizedVersion(snapshot.cliVersion);
  return `stale since ${time}, ${cli}${version ? ` ${version}` : ''} probe failed`;
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
