import { describe, expect, it } from 'vitest';
import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';
import { summarizeStatusBarHostLoad } from './status-bar-host-load';

function remoteHost(
  load1: number,
  cpuCores = 12,
  activeSlots = 0,
  timestamp = new Date().toISOString(),
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

  it('attributes elevated host load to Review-plane slots without double-counting host load', () => {
    const now = '2026-08-11T22:00:00.000Z';
    const host = remoteHost(8.4, 12, 0, now);
    host.executionPlanes = [
      {
        role: 'coding', runnerId: 'remote-1', name: 'remote-1', lastSeenAt: now,
        observedAt: now, activeSlots: 0, maxParallelism: 2, load1: 8.4, cpuCores: 12,
      },
      {
        role: 'review', runnerId: 'remote-1-review', name: 'remote-1-review', lastSeenAt: now,
        observedAt: now, activeSlots: 4, maxParallelism: 6, load1: 8.4, cpuCores: 12,
      },
    ];

    expect(summarizeStatusBarHostLoad([host], 0, Date.parse(now))).toMatchObject({
      load1: 8.4,
      cpuCores: 12,
      activeSlots: 4,
      codingSlots: 0,
      reviewSlots: 4,
      tone: 'working',
      correlation: 'consistent',
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
