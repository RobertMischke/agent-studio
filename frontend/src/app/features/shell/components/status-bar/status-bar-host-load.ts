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

/** A host qualifies for the load signal while it has stats and is neither offline nor retired. */
function isLiveLoadHost(host: RemoteHost): boolean {
  return host.stats !== null && host.status !== 'offline' && host.status !== 'retired';
}

/** Slot utilization comes from the runner heartbeat and does not depend on telemetry history loading. */
function isLiveSlotHost(host: RemoteHost): boolean {
  return host.role === 'remote' && host.status !== 'offline' && host.status !== 'retired';
}

/** The host's freshest telemetry point, or null when it is missing or older than {@link MAX_TELEMETRY_AGE_MS}. */
function freshPoint(host: RemoteHost, nowMs: number): HostTelemetryPoint | null {
  const point = host.telemetry?.points.at(-1) ?? null;
  if (point === null || point.load1 === null || point.cpuCores <= 0) return null;
  const observedAt = Date.parse(point.timestamp);
  if (!Number.isFinite(observedAt) || nowMs - observedAt > MAX_TELEMETRY_AGE_MS) return null;
  return point;
}

function freshSlotPoint(host: RemoteHost, nowMs: number): HostTelemetryPoint | null {
  const point = host.telemetry?.points.at(-1) ?? null;
  if (point === null) return null;
  const observedAt = Date.parse(point.timestamp);
  return Number.isFinite(observedAt) && nowMs - observedAt <= MAX_TELEMETRY_AGE_MS ? point : null;
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
    .filter(isLiveLoadHost)
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
   * (the daemon-reported `effectiveMaxParallelism`, the active+available
   * heartbeat split, or the desired runtime capacity). Null when any connected
   * host in the plane lacks a ceiling, so the footer never presents a partial
   * fleet-wide maximum as complete.
   */
  ceiling: number | null;
  /** Per-runner detail retained for the tooltip while the primary label stays compact. */
  hosts: readonly StatusBarHostSlots[];
}

export interface StatusBarHostSlots {
  id: string;
  name: string;
  active: number;
  ceiling: number | null;
}

export interface StatusBarSlotsByRole {
  coding: StatusBarPlaneSlots;
  review: StatusBarPlaneSlots;
}

function hostSlotCeiling(host: RemoteHost): number | null {
  // The daemon-reported effective value is the ceiling actually used by its
  // "into slot N/M" claim path. A desired central value can be newer than the
  // daemon's applied configuration, so it must not win while adoption is pending.
  if (host.effectiveMaxParallelism !== null && host.effectiveMaxParallelism !== undefined) {
    return host.effectiveMaxParallelism;
  }
  if (host.activeTaskCount !== undefined && host.availableSlots !== undefined) {
    return host.activeTaskCount + host.availableSlots;
  }
  if (host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  return null;
}

function hostActiveSlots(host: RemoteHost, nowMs: number): number {
  if (host.activeTaskCount !== undefined) return Math.max(0, host.activeTaskCount);
  return Math.max(0, freshSlotPoint(host, nowMs)?.activeSlots ?? 0);
}

/**
 * Split live remote execution slots and configured ceilings by executor plane
 * so the footer can show "remote x/N - review y/M" instead of a runner-host
 * count or one merged figure that hides review-plane load.
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
    coding: { active: 0, ceiling: null, hosts: [] },
    review: { active: 0, ceiling: null, hosts: [] },
  };
  for (const host of hosts) {
    if (!isLiveSlotHost(host)) continue;
    const plane = result[hostExecutorRole(host)];
    const active = hostActiveSlots(host, nowMs);
    const ceiling = hostSlotCeiling(host);
    plane.active += active;
    plane.hosts = [...plane.hosts, { id: host.id, name: host.name, active, ceiling }];
  }
  for (const plane of [result.coding, result.review]) {
    // A partial fleet denominator would look precise while under-counting
    // capacity. Show N/M only when every connected host reports M.
    plane.ceiling = plane.hosts.length > 0 && plane.hosts.every(host => host.ceiling !== null)
      ? plane.hosts.reduce((sum, host) => sum + (host.ceiling ?? 0), 0)
      : null;
  }
  return result;
}
