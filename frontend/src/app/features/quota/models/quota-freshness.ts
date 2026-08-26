import type { QuotaSnapshot } from './quota.model';

export interface QuotaFreshness {
  stale: boolean;
  label: string;
}

/**
 * Shared freshness projection for every quota surface. A failed refresh is
 * always stale even when the retained values are younger than the normal TTL,
 * and it carries the CLI version that attempted the failed observation.
 */
export function quotaFreshness(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  now: number,
): QuotaFreshness {
  const failedMs = snapshot.probeFailedAt ? Date.parse(snapshot.probeFailedAt) : NaN;
  if (Number.isFinite(failedMs)) {
    const version = normalizeCliVersion(snapshot.cliType, snapshot.cliVersion);
    return {
      stale: true,
      label: `probe failed ${formatClock(failedMs)}, ${snapshot.cliType}${version ? ` ${version}` : ''}`,
    };
  }

  const fetchedMs = snapshot.fetchedAt ? Date.parse(snapshot.fetchedAt) : NaN;
  const ageMs = Number.isFinite(fetchedMs)
    ? Math.max(0, now - fetchedMs)
    : Number.POSITIVE_INFINITY;
  return {
    stale: !snapshot.fetchedAt || ageMs > ttlMs,
    label: !snapshot.fetchedAt ? 'never refreshed' : `updated ${formatAgo(ageMs)}`,
  };
}

function normalizeCliVersion(cliType: string, raw: string | null | undefined): string | null {
  if (!raw?.trim()) return null;
  return raw.trim().replace(new RegExp(`^${escapeRegExp(cliType)}(?:-cli)?\\s+`, 'i'), '');
}

function formatClock(timestamp: number): string {
  const date = new Date(timestamp);
  return `${date.getHours().toString().padStart(2, '0')}:${date.getMinutes().toString().padStart(2, '0')}`;
}

function formatAgo(ms: number): string {
  if (!Number.isFinite(ms)) return 'never';
  const sec = Math.floor(ms / 1000);
  if (sec < 5) return 'just now';
  if (sec < 60) return `${sec} s ago`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min} min ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr} h ago`;
  return `${Math.floor(hr / 24)} d ago`;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
