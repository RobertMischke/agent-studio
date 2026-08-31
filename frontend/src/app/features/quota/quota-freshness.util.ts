import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.stale || snapshot.probeFailedAt) return true;
  const capturedAt = snapshot.capturedAt ?? snapshot.fetchedAt;
  const capturedMs = capturedAt ? Date.parse(capturedAt) : NaN;
  return !Number.isFinite(capturedMs) || Math.max(0, now - capturedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const capturedAt = snapshot.capturedAt ?? snapshot.fetchedAt;
  const staleSince = formatClock(capturedAt);
  const failedAt = formatClock(snapshot.probeFailedAt);
  const cli = snapshot.cliType.toLowerCase();
  const version = normalizedVersion(snapshot.probeCliVersion ?? snapshot.cliVersion);
  return `stale since ${staleSince}, probe failed ${failedAt}, ${cli}${version ? ` ${version}` : ''}`;
}

function formatClock(raw: string | null | undefined): string {
  if (!raw) return 'unknown time';
  const milliseconds = Date.parse(raw);
  return Number.isFinite(milliseconds)
    ? new Date(milliseconds).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
