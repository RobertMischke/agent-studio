import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';

export type StatusBarLoadTone = 'unknown' | 'calm' | 'working' | 'hot' | 'mismatch';
export type StatusBarLoadCorrelation = 'unknown' | 'consistent' | 'load-without-runs' | 'runs-without-load';

export interface StatusBarHostLoad {
  load1: number;
  cpuCores: number;
  activeSlots: number;
  ratio: number;
  tone: StatusBarLoadTone;
  correlation: StatusBarLoadCorrelation;
}

const MAX_TELEMETRY_AGE_MS = 5 * 60_000;

/**
 * Fold fresh remote-runner telemetry into the tiny status-bar signal.
 * `stats` and the point timestamp both guard freshness: the service clears
 * stats for stale points when hydrating, while the timestamp keeps a
 * long-lived status bar from presenting an old sample as current.
 */
export function summarizeStatusBarHostLoad(
  hosts: readonly RemoteHost[],
  runningCount: number,
  nowMs = Date.now(),
): StatusBarHostLoad | null {
  const points = hosts
    .filter(host =>
      host.role === 'remote'
      && host.stats !== null
      && host.status !== 'offline'
      && host.status !== 'retired')
    .map(host => host.telemetry?.points.at(-1) ?? null)
    .filter((point): point is HostTelemetryPoint =>
      point !== null
      && point.load1 !== null
      && point.cpuCores > 0
      && Number.isFinite(Date.parse(point.timestamp))
      && nowMs - Date.parse(point.timestamp) <= MAX_TELEMETRY_AGE_MS);

  if (points.length === 0) return null;

  const load1 = points.reduce((sum, point) => sum + (point.load1 ?? 0), 0);
  const cpuCores = points.reduce((sum, point) => sum + point.cpuCores, 0);
  const activeSlots = points.reduce((sum, point) => sum + point.activeSlots, 0);
  const ratio = load1 / cpuCores;
  const correlation: StatusBarLoadCorrelation =
    runningCount === 0 && ratio >= 0.5
      ? 'load-without-runs'
      : runningCount >= 2 && ratio <= 0.1
        ? 'runs-without-load'
        : 'consistent';
  const tone: StatusBarLoadTone = correlation !== 'consistent'
    ? 'mismatch'
    : ratio >= 1
      ? 'hot'
      : ratio >= 0.5
        ? 'working'
        : 'calm';

  return { load1, cpuCores, activeSlots, ratio, tone, correlation };
}
