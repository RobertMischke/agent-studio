import { describe, expect, it } from 'vitest';
import type { RemoteHost } from './remote-host.model';
import { groupPhysicalHosts } from './physical-host-group';

describe('groupPhysicalHosts', () => {
  it('uses advertised host identity and the freshest machine telemetry once', () => {
    const coding = host('agent-runner-01', 'coding', 61, '2026-08-12T07:00:00Z');
    const review = host('agent-runner-01-review', 'review', 37, '2026-08-12T07:01:00Z');

    const group = expectSingle(groupPhysicalHosts([coding, review], false));

    expect(group.id).toBe('agent-runner-01');
    expect(group.roles.map(role => role.serviceRole)).toEqual(['coding', 'review']);
    expect(group.machine.stats?.cpuLoadPct).toBe(37);
    expect(group.machine.lastHeartbeatAt).toBe('2026-08-12T07:01:00Z');
  });

  it('hides retired roles by default and reveals them without splitting a machine', () => {
    const coding = host('coding', 'coding', 20, '2026-08-12T07:00:00Z');
    const retired = { ...host('review', 'review', 20, '2026-08-11T07:00:00Z'), status: 'retired' as const };

    expect(expectSingle(groupPhysicalHosts([coding, retired], false)).roles.map(role => role.id))
      .toEqual(['coding']);
    expect(expectSingle(groupPhysicalHosts([coding, retired], true)).roles.map(role => role.id))
      .toEqual(['coding', 'review']);
  });

  it('keeps the runner name for older snapshots without an advertised host identity', () => {
    const legacy = { ...host('legacy-runner', 'coding', 20, '2026-08-12T07:00:00Z'), capacityHostId: null };

    const group = expectSingle(groupPhysicalHosts([legacy], false));

    expect(group.id).toBe('runner:legacy-runner');
    expect(group.name).toBe('legacy-runner');
    expect(group.machine.name).toBe('legacy-runner');
  });
});

function host(
  id: string,
  serviceRole: RemoteHost['serviceRole'],
  cpuLoadPct: number,
  observedAt: string,
): RemoteHost {
  return {
    id,
    name: id,
    role: 'remote',
    serviceRole,
    address: null,
    clientId: id,
    capacityHostId: 'agent-runner-01',
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: observedAt,
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: {
      ramTotalMb: 1024,
      ramFreeMb: 512,
      cpuCores: 4,
      cpuModel: 'Test',
      cpuLoadPct,
      diskTotalGb: 10,
      diskFreeGb: 5,
    },
    telemetry: {
      clientId: id,
      window: '1h',
      findings: [],
      points: [{
        timestamp: observedAt,
        cpuPercent: cpuLoadPct,
        load1: 1,
        load5: 1,
        load15: 1,
        memoryUsedBytes: 512,
        memoryTotalBytes: 1024,
        swapInBytesPerSecond: 0,
        swapOutBytesPerSecond: 0,
        cpuStealPercent: 0,
        ioWaitPercent: 0,
        cpuCores: 4,
        activeSlots: 0,
      }],
    },
  };
}

function expectSingle(groups: ReturnType<typeof groupPhysicalHosts>) {
  expect(groups).toHaveLength(1);
  return groups[0];
}
