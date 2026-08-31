import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (typeof snapshot.stale === 'boolean') return snapshot.stale;
  if (snapshot.probeFailedAt) return true;
  const capturedAt = snapshot.capturedAt ?? snapshot.fetchedAt;
  const fetchedMs = capturedAt ? Date.parse(capturedAt) : NaN;
  return !Number.isFinite(fetchedMs) || Math.max(0, now - fetchedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt && !snapshot.probeFailed) return null;
  const failedMs = Date.parse(snapshot.probeFailedAt ?? '');
  const failedTime = Number.isFinite(failedMs)
    ? new Date(failedMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const captured = snapshot.capturedAt ?? snapshot.fetchedAt;
  const capturedMs = captured ? Date.parse(captured) : NaN;
  const staleSince = Number.isFinite(capturedMs)
    ? new Date(capturedMs).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
  const cli = snapshot.cliType.toLowerCase();
  const version = normalizedVersion(snapshot.cliVersion);
  return `stale since ${staleSince}, probe failed ${failedTime} · ${cli}${version ? ` ${version}` : ''}`;
}

export function quotaSafeProbeError(error: string | null | undefined): string | null {
  if (!error?.trim()) return null;
  return /(?:task|operation) was cancel(?:l)?ed/i.test(error)
    ? 'Quota probe timed out before the CLI panel rendered.'
    : error;
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
