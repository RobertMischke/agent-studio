import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';
import { freshHostTelemetry, hostExecutorRole, RUNNING_TELEMETRY_FRESH_MS } from '../../../remote-hosts';

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
  /** Null means at least one connected host has no fresh slot heartbeat. */
  active: number | null;
  /**
   * Sum of every contributing host's own configured slot ceiling
   * (`runtimeCapacity.maxParallelism`, the daemon-reported
   * `effectiveMaxParallelism`, or the active+available split - same priority
   * order the host card uses). Null when any connected host in the plane does
   * not report one, so the footer never presents a partial fleet maximum as a
   * complete total (AGT-2645).
   */
  ceiling: number | null;
  hosts: readonly StatusBarPlaneHostSlots[];
}

export interface StatusBarPlaneHostSlots {
  id: string;
  name: string;
  active: number | null;
  ceiling: number | null;
}

export interface StatusBarSlotsByRole {
  coding: StatusBarPlaneSlots;
  review: StatusBarPlaneSlots;
}

function hostSlotCeiling(host: RemoteHost): number | null {
  const role = hostExecutorRole(host);
  // Review is a separately configured executor plane. Its role-local daemon
  // ceiling must win over the physical host's coding capacity record.
  if (role === 'review' && host.roleMaxParallelism !== null && host.roleMaxParallelism !== undefined) {
    return host.roleMaxParallelism;
  }
  if (role === 'coding' && host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  if (host.effectiveMaxParallelism !== null && host.effectiveMaxParallelism !== undefined) {
    return host.effectiveMaxParallelism;
  }
  if (host.roleMaxParallelism !== null && host.roleMaxParallelism !== undefined) {
    return host.roleMaxParallelism;
  }
  if (host.activeTaskCount !== undefined && host.availableSlots !== undefined) {
    return host.activeTaskCount + host.availableSlots;
  }
  if (host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  return null;
}

function isConnectedRemoteHost(host: RemoteHost, nowMs: number): boolean {
  if (host.role !== 'remote' || host.status === 'offline' || host.status === 'retired') return false;
  const heartbeatAt = host.lastHeartbeatAt ? Date.parse(host.lastHeartbeatAt) : Number.NaN;
  return Number.isFinite(heartbeatAt) && nowMs - heartbeatAt <= RUNNING_TELEMETRY_FRESH_MS;
}

/**
 * Split active execution slots and configured ceilings by executor plane
 * (coding vs review) so the footer can show "coding x/N - review y/M"
 * instead of one merged figure that hides review-plane load (AGT-2645).
 * Coding and review daemons register as separate RunnerIds even on a shared
 * physical host, so each remote `RemoteHost` belongs to exactly one plane
 * ({@link hostExecutorRole}). Local execution remains visible separately and
 * never inflates the remote coding figure.
 */
export function summarizeStatusBarSlotsByRole(
  hosts: readonly RemoteHost[],
  nowMs = Date.now(),
): StatusBarSlotsByRole {
  const result: StatusBarSlotsByRole = {
    coding: { active: null, ceiling: null, hosts: [] },
    review: { active: null, ceiling: null, hosts: [] },
  };
  for (const role of ['coding', 'review'] as const) {
    const details = hosts
      .filter(host => isConnectedRemoteHost(host, nowMs) && hostExecutorRole(host) === role)
      .map(host => ({
        id: host.id,
        name: host.name,
        active: freshHostTelemetry(host, nowMs)?.activeSlots ?? null,
        ceiling: hostSlotCeiling(host),
      }));
    result[role] = {
      active: details.length > 0 && details.every(host => host.active !== null)
        ? details.reduce((sum, host) => sum + (host.active ?? 0), 0)
        : null,
      ceiling: details.length > 0 && details.every(host => host.ceiling !== null)
        ? details.reduce((sum, host) => sum + (host.ceiling ?? 0), 0)
        : null,
      hosts: details,
    };
  }
  return result;
}
