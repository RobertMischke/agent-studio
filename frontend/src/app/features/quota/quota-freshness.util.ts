import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.probeFailedAt) return true;
  const capturedAt = snapshot.capturedAt ?? snapshot.fetchedAt;
  const capturedMs = capturedAt ? Date.parse(capturedAt) : NaN;
  return snapshot.stale === true
    || !Number.isFinite(capturedMs)
    || Math.max(0, now - capturedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const staleSince = snapshot.staleSince ?? snapshot.probeFailedAt;
  const staleMs = Date.parse(staleSince);
  const time = Number.isFinite(staleMs)
    ? new Date(staleMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const cli = snapshot.cliType.toLowerCase();
  const version = normalizedVersion(snapshot.cliVersion);
  return `stale since ${time}, probe failed · ${cli}${version ? ` ${version}` : ''}`;
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
