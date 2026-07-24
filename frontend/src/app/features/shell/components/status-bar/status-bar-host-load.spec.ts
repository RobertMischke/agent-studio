import { describe, expect, it } from 'vitest';
import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';
import { summarizeStatusBarHostLoad } from './status-bar-host-load';

function remoteHost(load1: number, cpuCores = 12, activeSlots = 0): RemoteHost {
  const point: HostTelemetryPoint = {
    timestamp: new Date().toISOString(),
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

  it('quietly flags reported runs with almost no host load', () => {
    expect(summarizeStatusBarHostLoad([remoteHost(0.2)], 2)).toMatchObject({
      tone: 'mismatch',
      correlation: 'runs-without-load',
    });
  });

  it('does not surface stale historical telemetry', () => {
    const stale = { ...remoteHost(9), stats: null };
    expect(summarizeStatusBarHostLoad([stale], 0)).toBeNull();
  });
});
