import type { QuotaSnapshot } from './models/quota.model';

export function quotaSnapshotIsStale(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): boolean {
  if (snapshot.probeFailedAt) return true;
  if (snapshot.stale === true) return true;
  const fetchedMs = Date.parse(snapshot.capturedAt ?? snapshot.fetchedAt ?? '');
  return !Number.isFinite(fetchedMs) || Math.max(0, now - fetchedMs) > ttlMs;
}

export function quotaProbeFailureLabel(snapshot: QuotaSnapshot): string | null {
  if (!snapshot.probeFailedAt) return null;
  const failedMs = Date.parse(snapshot.probeFailedAt);
  const capturedMs = Date.parse(snapshot.capturedAt ?? snapshot.fetchedAt ?? '');
  const failedTime = formatTime(failedMs);
  const capturedTime = formatTime(capturedMs);
  const cli = snapshot.cliType.slice(0, 1).toUpperCase() + snapshot.cliType.slice(1).toLowerCase();
  const version = normalizedVersion(snapshot.cliVersion);
  return `Stale since ${capturedTime}, probe failed ${failedTime} · ${cli}${version ? ` ${version}` : ''}`;
}

/** Keep runtime cancellation wording out of every quota surface, including old backends. */
export function quotaVisibleError(error: string | null | undefined): string | null {
  if (!error?.trim()) return null;
  return /(?:task|operation) was cancel(?:l)?ed/i.test(error)
    ? 'Quota probe timed out before the CLI panel rendered.'
    : error;
}

function formatTime(value: number): string {
  return Number.isFinite(value)
    ? new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : 'unknown time';
}

function normalizedVersion(raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim()
    .replace(/^codex-cli\s+/i, '')
    .replace(/^claude(?:\s+code)?\s+/i, '');
}
