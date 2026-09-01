import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';
import { hostExecutorRole } from '../../../remote-hosts';

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

/** A host qualifies for the live status-bar signal while it has stats and is neither offline nor retired. */
function isLiveHost(host: RemoteHost): boolean {
  return host.stats !== null && host.status !== 'offline' && host.status !== 'retired';
}

/** The host's freshest telemetry point, or null when it is missing or older than {@link MAX_TELEMETRY_AGE_MS}. */
function freshPoint(host: RemoteHost, nowMs: number): HostTelemetryPoint | null {
  const point = host.telemetry?.points.at(-1) ?? null;
  if (point === null || point.load1 === null || point.cpuCores <= 0) return null;
  const observedAt = Date.parse(point.timestamp);
  if (!Number.isFinite(observedAt) || nowMs - observedAt > MAX_TELEMETRY_AGE_MS) return null;
  return point;
}

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
  const points = hosts
    .filter(isLiveHost)
    .map(host => freshPoint(host, nowMs))
    .filter((point): point is HostTelemetryPoint => point !== null);

  if (points.length === 0) return null;

  const load1 = points.reduce((sum, point) => sum + (point.load1 ?? 0), 0);
  const cpuCores = points.reduce((sum, point) => sum + point.cpuCores, 0);
  const activeSlots = points.reduce((sum, point) => sum + point.activeSlots, 0);
  const ratio = load1 / cpuCores;
  // `activeSlots` covers both coding and review workers reported by all daemons.
  // Only flag `load-without-runs` when no plane has any active work at all;
  // review workers showing up in `activeSlots` but absent from the coding
  // board lane must not trigger a false consistency hint.
  const correlation: StatusBarLoadCorrelation =
    runningCount === 0 && activeSlots === 0 && ratio >= 0.5
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

export interface StatusBarPlaneSlots {
  active: number;
  /**
   * Sum of every contributing host's own configured slot ceiling
   * (role-local maximum for review; applied/runtime capacity for coding, with
   * compatibility fallbacks). Null when no host in the plane reports one, so
   * the footer never fabricates a fleet-wide maximum.
   */
  ceiling: number | null;
  hosts: readonly StatusBarPlaneHost[];
}

export interface StatusBarPlaneHost {
  id: string;
  physicalHostId: string;
  name: string;
  active: number;
  ceiling: number | null;
}

export interface StatusBarSlotsByRole {
  remote: StatusBarPlaneSlots;
  review: StatusBarPlaneSlots;
}

function hostSlotCeiling(host: RemoteHost, role: 'coding' | 'review'): number | null {
  // Review is a separate daemon plane and does not adopt the coding host's
  // centrally managed capacity. Its RUNNER_MAX_PARALLELISM is authoritative.
  if (role === 'review' && host.roleMaxParallelism !== null && host.roleMaxParallelism !== undefined) {
    return host.roleMaxParallelism;
  }
  if (host.effectiveMaxParallelism !== null && host.effectiveMaxParallelism !== undefined) {
    return host.effectiveMaxParallelism;
  }
  if (host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  if (host.roleMaxParallelism !== null && host.roleMaxParallelism !== undefined) {
    return host.roleMaxParallelism;
  }
  if (host.activeTaskCount !== undefined && host.availableSlots !== undefined) {
    return host.activeTaskCount + host.availableSlots;
  }
  return null;
}

function freshSlotCount(host: RemoteHost, nowMs: number): number | null {
  if (host.status === 'offline' || host.status === 'retired') return null;
  if (host.executorActiveSlots !== null && host.executorActiveSlots !== undefined) {
    const observedAt = Date.parse(host.executorSlotsObservedAt ?? '');
    if (Number.isFinite(observedAt) && nowMs - observedAt <= MAX_TELEMETRY_AGE_MS) {
      return Math.max(0, host.executorActiveSlots);
    }
  }

  const point = host.telemetry?.points.at(-1) ?? null;
  const observedAt = point ? Date.parse(point.timestamp) : Number.NaN;
  return point && Number.isFinite(observedAt) && nowMs - observedAt <= MAX_TELEMETRY_AGE_MS
    ? Math.max(0, point.activeSlots)
    : null;
}

/**
 * Split active execution slots and configured ceilings by remote executor plane
 * so the footer can show "remote x/N - review y/M"
 * instead of one merged figure that hides review-plane load (AGT-2698).
 * Coding and review daemons register as separate RunnerIds even on a shared
 * physical host, so each `RemoteHost` belongs to exactly one plane
 * ({@link hostExecutorRole}) and this is a plain filter + reduce over the
 * same host list {@link summarizeStatusBarHostLoad} already consumes.
 */
export function summarizeStatusBarSlotsByRole(
  hosts: readonly RemoteHost[],
  nowMs = Date.now(),
): StatusBarSlotsByRole {
  const result: StatusBarSlotsByRole = {
    remote: { active: 0, ceiling: null, hosts: [] },
    review: { active: 0, ceiling: null, hosts: [] },
  };
  for (const host of hosts) {
    if (host.role !== 'remote') continue;
    const role = hostExecutorRole(host);
    const plane = role === 'review' ? result.review : result.remote;
    const active = freshSlotCount(host, nowMs);
    if (active === null) continue;
    const ceiling = hostSlotCeiling(host, role);
    plane.active += active;
    if (ceiling !== null) plane.ceiling = (plane.ceiling ?? 0) + ceiling;
    plane.hosts = [...plane.hosts, {
      id: host.id,
      physicalHostId: host.capacityHostId ?? host.id,
      name: host.name,
      active,
      ceiling,
    }];
  }
  return result;
}
