import type { TaskInfo } from '../../../models/task.model';
import { deriveActiveTaskRun } from '../../../services/run-activity.util';
import type { HostTelemetryPoint, RemoteHost } from './remote-host.model';

export const RUNNING_TELEMETRY_FRESH_MS = 5 * 60_000;

export interface BoardRunningTruth {
  local: number;
  remote: number;
  total: number;
  remoteByRunnerId: ReadonlyMap<string, number>;
}

/**
 * The board's Progress lane is the canonical inventory of active task runs.
 * Local processes and fresh remote leases are reconciled by the shared active
 * run projection. A canonical disconnected location therefore stays outside
 * both buckets even when a compatibility runner badge is still present.
 */
export function deriveBoardRunningTruth(progress: readonly TaskInfo[]): BoardRunningTruth {
  let local = 0;
  let remote = 0;
  const remoteByRunnerId = new Map<string, number>();

  for (const task of progress) {
    if (task.state !== '3-progress') continue;

    const active = deriveActiveTaskRun(task);
    if (active?.kind === 'remote') {
      remote++;
      if (active.runnerId) {
        remoteByRunnerId.set(active.runnerId, (remoteByRunnerId.get(active.runnerId) ?? 0) + 1);
      }
      continue;
    }

    if (active?.kind === 'local') local++;
  }

  return { local, remote, total: local + remote, remoteByRunnerId };
}

/** Latest telemetry sample, independent of the history window selected in UI. */
export function latestHostTelemetry(host: RemoteHost): HostTelemetryPoint | null {
  return host.telemetry?.points.at(-1) ?? null;
}

/**
 * Active-slot telemetry is live only while both the daemon heartbeat and the
 * sample itself are inside the same five-minute freshness window.
 */
export function freshHostTelemetry(
  host: RemoteHost,
  nowMs = Date.now(),
): HostTelemetryPoint | null {
  if (host.status === 'offline' || host.status === 'retired') return null;
  const heartbeatAt = host.lastHeartbeatAt ? Date.parse(host.lastHeartbeatAt) : Number.NaN;
  if (!Number.isFinite(heartbeatAt) || nowMs - heartbeatAt > RUNNING_TELEMETRY_FRESH_MS) return null;

  const point = latestHostTelemetry(host);
  const observedAt = point ? Date.parse(point.timestamp) : Number.NaN;
  if (!point || !Number.isFinite(observedAt) || nowMs - observedAt > RUNNING_TELEMETRY_FRESH_MS) return null;
  return point;
}

export function boardRemoteSlotsForHost(truth: BoardRunningTruth, host: RemoteHost): number {
  const ids = new Set([host.id, host.clientId]);
  let count = 0;
  for (const [runnerId, runnerCount] of truth.remoteByRunnerId) {
    if (ids.has(runnerId)) count += runnerCount;
  }
  return count;
}

/** Sum only fresh remote-host samples. Null means there is no live comparison source. */
export function freshRemoteTelemetrySlots(
  hosts: readonly RemoteHost[],
  nowMs = Date.now(),
): number | null {
  const samples = hosts
    .filter(host => host.role === 'remote')
    .map(host => freshHostTelemetry(host, nowMs))
    .filter((point): point is HostTelemetryPoint => point !== null);
  if (samples.length === 0) return null;
  return samples.reduce((sum, point) => sum + point.activeSlots, 0);
}
