import type { QuotaSnapshot } from './quota.model';

export interface QuotaFreshness {
  stale: boolean;
  label: string;
  tooltip: string;
}

export function buildQuotaFreshness(
  snapshot: QuotaSnapshot,
  ttlMs: number,
  nowMs: number,
  locale?: string,
  timeZone?: string,
): QuotaFreshness {
  if (snapshot.error) {
    const failedAt = snapshot.probeFailedAt ? Date.parse(snapshot.probeFailedAt) : NaN;
    const failureTime = Number.isFinite(failedAt)
      ? new Intl.DateTimeFormat(locale, {
          hour: '2-digit',
          minute: '2-digit',
          hour12: false,
          ...(timeZone ? { timeZone } : {}),
        }).format(new Date(failedAt))
      : 'unknown time';
    const version = shortCliVersion(snapshot.cliVersion);
    const identity = version ? `${snapshot.cliType} ${version}` : snapshot.cliType;
    const label = `probe failed ${failureTime}, ${identity}`;
    return { stale: true, label, tooltip: `${label}: ${snapshot.error}` };
  }

  const fetchedAt = snapshot.fetchedAt ? Date.parse(snapshot.fetchedAt) : NaN;
  const ageMs = Number.isFinite(fetchedAt)
    ? Math.max(0, nowMs - fetchedAt)
    : Number.POSITIVE_INFINITY;
  const label = !snapshot.fetchedAt ? 'never refreshed' : `updated ${formatAgo(ageMs)}`;
  return { stale: !snapshot.fetchedAt || ageMs > ttlMs, label, tooltip: label };
}

function shortCliVersion(raw: string | null | undefined): string | null {
  if (!raw) return null;
  return raw.match(/\d+\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9.-]+)?/)?.[0] ?? raw.trim();
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
