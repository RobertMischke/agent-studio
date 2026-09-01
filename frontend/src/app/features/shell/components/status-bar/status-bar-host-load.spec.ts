import { describe, expect, it } from 'vitest';
import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';
import { summarizeStatusBarHostLoad, summarizeStatusBarSlotsByRole } from './status-bar-host-load';

function remoteHost(
  load1: number,
  cpuCores = 12,
  activeSlots = 0,
  timestamp = new Date().toISOString(),
  options: { executorRole?: 'coding' | 'review'; maxParallelism?: number } = {},
): RemoteHost {
  const point: HostTelemetryPoint = {
    timestamp,
    cpuPercent: 40,
    load1,
    load5: load1,
    load15: load1,
    memoryUsedBytes: 4_000_000_000,
    memoryTotalBytes: 16_000_000_000,
    swapInBytesPerSecond: 0,
    swapOutBytesPerSecond: 0,
    cpuStealPercent: 0,
    ioWaitPercent: 0,
    cpuCores,
    activeSlots,
  };
  return {
    id: 'remote-1',
    name: 'remote-1',
    role: 'remote',
    address: 'remote-1',
    clientId: 'remote-1',
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: point.timestamp,
    uptimeLabel: '1d',
    capabilities: [],
    cliQuotas: [],
    stats: {
      ramTotalMb: 16_000,
      ramFreeMb: 8_000,
      cpuCores,
      cpuModel: 'test',
      cpuLoadPct: 40,
      diskTotalGb: 100,
      diskFreeGb: 50,
    },
    telemetry: { clientId: 'remote-1', window: '14d', points: [point], findings: [] },
    ...(options.executorRole === 'review'
      ? {
          capabilityHealth: [{
            key: 'executor:review',
            category: 'executor',
            advertisedStatus: 'ready',
            healthState: 'healthy' as const,
            advertisedAt: timestamp,
            freshUntil: timestamp,
            isFresh: true,
            consecutiveFailures: 0,
            affectedClaims: [],
            recoveryHistory: [],
          }],
        }
      : {}),
    ...(options.maxParallelism !== undefined
      ? {
          runtimeCapacity: {
            hostId: 'remote-1',
            maxParallelism: options.maxParallelism,
            targetLoadPercent: 80,
            rampStrategy: 'balanced' as const,
            version: 1,
            updatedAt: timestamp,
          },
        }
      : {}),
  };
}

describe('summarizeStatusBarHostLoad', () => {
  it('treats several runs plus elevated host load as consistent', () => {
    expect(summarizeStatusBarHostLoad([remoteHost(7.2, 12, 4)], 4)).toMatchObject({
      tone: 'working',
      correlation: 'consistent',
      activeSlots: 4,
    });
  });

  it('quietly flags high load without reported runs', () => {
    expect(summarizeStatusBarHostLoad([remoteHost(8.4)], 0)).toMatchObject({
      tone: 'mismatch',
      correlation: 'load-without-runs',
    });
  });

  it('quietly flags reported runs with almost no host load', () => {
    expect(summarizeStatusBarHostLoad([remoteHost(0.2)], 2)).toMatchObject({
      tone: 'mismatch',
      correlation: 'runs-without-load',
    });
  });

  it('keeps a single quiet run from becoming a false mismatch', () => {
    expect(summarizeStatusBarHostLoad([remoteHost(0.2)], 1)).toMatchObject({
      tone: 'calm',
      correlation: 'consistent',
    });
  });

  it('does not surface stale historical telemetry even while stats remain populated', () => {
    const now = Date.parse('2026-07-24T20:10:00.000Z');
    const stale = remoteHost(9, 12, 0, '2026-07-24T20:00:00.000Z');
    expect(summarizeStatusBarHostLoad([stale], 0, now)).toBeNull();
  });

  it('includes local execution-host telemetry in the aggregate', () => {
    const local = {
      ...remoteHost(1.5, 4, 1),
      id: 'local',
      name: 'Local machine',
      role: 'local' as const,
      address: null,
      clientId: 'local-default',
    };

    expect(summarizeStatusBarHostLoad([local, remoteHost(2.5, 8, 2)], 3)).toMatchObject({
      load1: 4,
      cpuCores: 12,
      activeSlots: 3,
    });
  });
});

describe('summarizeStatusBarSlotsByRole', () => {
  it('splits active slots by executor plane so review load never inflates the coding figure', () => {
    const coding = remoteHost(1, 8, 2, undefined, { maxParallelism: 8 });
    const review = { ...remoteHost(1, 8, 3, undefined, { executorRole: 'review', maxParallelism: 6 }), id: 'remote-2', clientId: 'remote-2' };

    expect(summarizeStatusBarSlotsByRole([coding, review])).toEqual({
      remote: {
        active: 2,
        ceiling: 8,
        hosts: [{ id: 'remote-1', physicalHostId: 'remote-1', name: 'remote-1', active: 2, ceiling: 8 }],
      },
      review: {
        active: 3,
        ceiling: 6,
        hosts: [{ id: 'remote-2', physicalHostId: 'remote-2', name: 'remote-1', active: 3, ceiling: 6 }],
      },
    });
  });

  it('keeps local execution out of the remote coding-plane figure', () => {
    const local = {
      ...remoteHost(1, 4, 1),
      id: 'local',
      name: 'Local machine',
      role: 'local' as const,
      address: null,
      clientId: 'local-default',
    };

    expect(summarizeStatusBarSlotsByRole([local])).toEqual({
      remote: { active: 0, ceiling: null, hosts: [] },
      review: { active: 0, ceiling: null, hosts: [] },
    });
  });

  it('leaves ceiling null for a plane where no host reports a configured limit', () => {
    const review = { ...remoteHost(1, 8, 1, undefined, { executorRole: 'review' }), id: 'remote-2', clientId: 'remote-2' };

    expect(summarizeStatusBarSlotsByRole([review])).toMatchObject({
      review: { active: 1, ceiling: null },
    });
  });

  it('excludes offline and retired hosts from both active counts and ceilings', () => {
    const offline = { ...remoteHost(1, 8, 5, undefined, { maxParallelism: 8 }), status: 'offline' as const };

    expect(summarizeStatusBarSlotsByRole([offline])).toEqual({
      remote: { active: 0, ceiling: null, hosts: [] },
      review: { active: 0, ceiling: null, hosts: [] },
    });
  });

  it('sums several hosts and retains their individual utilization for the tooltip', () => {
    const berlin = {
      ...remoteHost(1, 8, 3, undefined, { maxParallelism: 4 }),
      id: 'runner-berlin',
      clientId: 'runner-berlin',
      name: 'agent-runner-berlin',
      capacityHostId: 'host-berlin',
    };
    const hamburg = {
      ...remoteHost(1, 8, 3, undefined, { maxParallelism: 4 }),
      id: 'runner-hamburg',
      clientId: 'runner-hamburg',
      name: 'agent-runner-hamburg',
      capacityHostId: 'host-hamburg',
    };

    expect(summarizeStatusBarSlotsByRole([berlin, hamburg]).remote).toEqual({
      active: 6,
      ceiling: 8,
      hosts: [
        {
          id: 'runner-berlin', physicalHostId: 'host-berlin', name: 'agent-runner-berlin',
          active: 3, ceiling: 4,
        },
        {
          id: 'runner-hamburg', physicalHostId: 'host-hamburg', name: 'agent-runner-hamburg',
          active: 3, ceiling: 4,
        },
      ],
    });
  });

  it('uses the review daemon role ceiling instead of the shared coding capacity', () => {
    const review = {
      ...remoteHost(1, 8, 2, undefined, { executorRole: 'review', maxParallelism: 8 }),
      roleMaxParallelism: 6,
    };

    expect(summarizeStatusBarSlotsByRole([review]).review).toMatchObject({
      active: 2,
      ceiling: 6,
    });
  });
});
