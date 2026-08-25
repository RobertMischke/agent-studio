import {
  hostExecutorRole,
  type HostHeartbeatStatus,
  type RemoteHost,
} from './remote-host.model';

export interface PhysicalHostGroup {
  /** Stable machine identity advertised by the runner capability contract. */
  id: string;
  name: string;
  machine: RemoteHost;
  roles: readonly RemoteHost[];
}

/** Group process identities by their advertised physical machine. */
export function groupPhysicalHosts(
  hosts: readonly RemoteHost[],
  includeRetired: boolean,
): PhysicalHostGroup[] {
  const visible = includeRetired ? hosts : hosts.filter(host => host.status !== 'retired');
  const buckets = new Map<string, RemoteHost[]>();
  for (const host of visible) {
    const id = physicalHostId(host);
    buckets.set(id, [...(buckets.get(id) ?? []), host]);
  }

  return [...buckets.entries()].map(([id, bucket]) => {
    const roles = [...bucket].sort(compareRoles);
    const detailRole = roles.find(role => role.role === 'local')
      ?? roles.find(role => hostExecutorRole(role) === 'coding')
      ?? roles[0];
    const telemetryRole = [...roles].sort(
      (left, right) => observationTime(right) - observationTime(left),
    )[0];
    const latestRole = [...roles].sort(
      (left, right) => heartbeatTime(right) - heartbeatTime(left),
    )[0];
    const name = detailRole.capacityHostId?.trim() || detailRole.name;
    const machine: RemoteHost = {
      ...detailRole,
      name,
      status: aggregateStatus(roles),
      lastHeartbeatAt: latestRole.lastHeartbeatAt,
      releaseId: latestRole.releaseId ?? detailRole.releaseId ?? null,
      capabilities: unique(roles.flatMap(role => role.capabilities)),
      capabilityHealth: uniqueBy(roles.flatMap(role => role.capabilityHealth ?? []), item => item.key),
      cliQuotas: uniqueBy(roles.flatMap(role => role.cliQuotas), item => `${item.cliType}|${item.windowLabel}`),
      projectPreflights: uniqueBy(roles.flatMap(role => role.projectPreflights ?? []), item => item.projectId),
      stats: telemetryRole.stats,
      telemetry: telemetryRole.telemetry,
      telemetryLoading: roles.some(role => role.telemetryLoading),
    };
    return { id, name, machine, roles };
  });
}

export function physicalHostId(host: RemoteHost): string {
  const advertised = host.capacityHostId?.trim();
  if (advertised) return advertised;
  return host.role === 'local' ? `local:${host.id}` : `runner:${host.id}`;
}

function compareRoles(left: RemoteHost, right: RemoteHost): number {
  const compared = roleOrder(left) - roleOrder(right);
  return compared || left.name.localeCompare(right.name, undefined, { numeric: true });
}

function roleOrder(host: RemoteHost): number {
  if (host.role === 'local') return 0;
  return hostExecutorRole(host) === 'coding' ? 1 : 2;
}

function aggregateStatus(roles: readonly RemoteHost[]): HostHeartbeatStatus {
  if (roles.every(role => role.status === 'retired')) return 'retired';
  if (roles.every(role => role.status === 'offline')) return 'offline';
  if (roles.some(role => role.status === 'degraded' || role.status === 'offline')) return 'degraded';
  if (roles.some(role => role.status === 'draining')) return 'draining';
  if (roles.some(role => role.status === 'online')) return 'online';
  return 'idle';
}

function observationTime(host: RemoteHost): number {
  const point = host.telemetry?.points.at(-1)?.timestamp;
  return timestamp(point ?? host.lastHeartbeatAt);
}

function heartbeatTime(host: RemoteHost): number {
  return timestamp(host.lastHeartbeatAt);
}

function timestamp(value: string | null | undefined): number {
  if (!value) return 0;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function unique<T>(items: readonly T[]): T[] {
  return [...new Set(items)];
}

function uniqueBy<T>(items: readonly T[], key: (item: T) => string): T[] {
  return [...new Map(items.map(item => [key(item), item])).values()];
}
