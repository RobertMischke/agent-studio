import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.probeFailedAt) return true;
  if (typeof snapshot.isStale === 'boolean') return snapshot.isStale;
  const captured = snapshot.capturedAt ?? snapshot.fetchedAt;
  const fetchedMs = captured ? Date.parse(captured) : NaN;
  return !Number.isFinite(fetchedMs) || Math.max(0, now - fetchedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const staleSince = snapshot.staleSince ?? snapshot.probeFailedAt;
  const failedMs = Date.parse(staleSince);
  const time = Number.isFinite(failedMs)
    ? new Date(failedMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const cli = snapshot.cliType.toLowerCase();
  const version = normalizedVersion(snapshot.cliVersion);
  return `Stale since ${time} · probe failed · ${cli}${version ? ` ${version}` : ''}`;
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
