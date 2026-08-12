import type { HostExecutionPlaneTelemetry, HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';
import { freshHostExecutionPlane } from '../../../remote-hosts';

export type StatusBarLoadTone = 'unknown' | 'calm' | 'working' | 'hot' | 'mismatch';
export type StatusBarLoadCorrelation = 'unknown' | 'consistent' | 'load-without-runs' | 'runs-without-load';

export interface StatusBarHostLoad {
  load1: number;
  cpuCores: number;
  activeSlots: number;
  codingSlots: number;
  reviewSlots: number;
  ratio: number;
  tone: StatusBarLoadTone;
  correlation: StatusBarLoadCorrelation;
}

const MAX_TELEMETRY_AGE_MS = 5 * 60_000;

/**
 * Fold fresh execution-host telemetry into the tiny status-bar signal.
 * `stats` and the point timestamp both guard freshness: the service clears
 * stats for stale points when hydrating, while the timestamp keeps a
 * long-lived status bar from presenting an old sample as current.
 */
export function summarizeStatusBarHostLoad(
  hosts: readonly RemoteHost[],
  runningCount: number,
  nowMs = Date.now(),
): StatusBarHostLoad | null {
  const samples = hosts
    .filter(host =>
      host.stats !== null
      && host.status !== 'offline'
      && host.status !== 'retired')
    .map(host => hostLoadSample(host, nowMs))
    .filter((sample): sample is HostLoadSample => sample !== null);

  if (samples.length === 0) return null;

  const load1 = samples.reduce((sum, sample) => sum + sample.load1, 0);
  const cpuCores = samples.reduce((sum, sample) => sum + sample.cpuCores, 0);
  const codingSlots = samples.reduce((sum, sample) => sum + sample.codingSlots, 0);
  const reviewSlots = samples.reduce((sum, sample) => sum + sample.reviewSlots, 0);
  const activeSlots = codingSlots + reviewSlots;
  const ratio = load1 / cpuCores;
  const correlation: StatusBarLoadCorrelation =
    runningCount === 0 && activeSlots === 0 && ratio >= 0.5
      ? 'load-without-runs'
      : Math.max(runningCount, activeSlots) >= 2 && ratio <= 0.1
        ? 'runs-without-load'
        : 'consistent';
  const tone: StatusBarLoadTone = correlation !== 'consistent'
    ? 'mismatch'
    : ratio >= 1
      ? 'hot'
      : ratio >= 0.5
        ? 'working'
        : 'calm';

  return { load1, cpuCores, activeSlots, codingSlots, reviewSlots, ratio, tone, correlation };
}

interface HostLoadSample {
  load1: number;
  cpuCores: number;
  codingSlots: number;
  reviewSlots: number;
}

function hostLoadSample(host: RemoteHost, nowMs: number): HostLoadSample | null {
  const coding = freshHostExecutionPlane(host, 'coding', nowMs);
  const review = freshHostExecutionPlane(host, 'review', nowMs);
  const freshestPlane = [coding, review]
    .filter((plane): plane is HostExecutionPlaneTelemetry => plane !== null)
    .filter(plane => plane.load1 !== null && plane.cpuCores > 0)
    .sort((a, b) => Date.parse(b.observedAt!) - Date.parse(a.observedAt!))[0];
  if (freshestPlane) {
    return {
      load1: freshestPlane.load1!,
      cpuCores: freshestPlane.cpuCores,
      codingSlots: coding?.activeSlots ?? 0,
      reviewSlots: review?.activeSlots ?? 0,
    };
  }

  const point = host.telemetry?.points.at(-1) ?? null;
  if (!freshPoint(point, nowMs)) return null;
  return {
    load1: point.load1,
    cpuCores: point.cpuCores,
    codingSlots: point.activeSlots,
    reviewSlots: 0,
  };
}

function freshPoint(point: HostTelemetryPoint | null, nowMs: number): point is HostTelemetryPoint & { load1: number } {
  return point !== null
    && point.load1 !== null
    && point.cpuCores > 0
    && Number.isFinite(Date.parse(point.timestamp))
    && nowMs - Date.parse(point.timestamp) <= MAX_TELEMETRY_AGE_MS;
}
