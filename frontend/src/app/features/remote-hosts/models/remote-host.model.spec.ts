import { describe, expect, it } from 'vitest';
import {
  clampPct,
  diskUsedPct,
  formatDisk,
  formatMemory,
  hostRoleLabel,
  hostStatusLabel,
  hostStatusTone,
  meterTone,
  ramUsedPct,
  relativeHeartbeat,
  type HostSystemStats,
} from './remote-host.model';

const stats: HostSystemStats = {
  ramTotalMb: 62 * 1024,
  ramFreeMb: 38 * 1024,
  cpuCores: 8,
  cpuModel: 'Xeon',
  cpuLoadPct: 54,
  diskTotalGb: 240,
  diskFreeGb: 96,
};

describe('remote-host.model helpers', () => {
  it('formats memory as GB and disk as whole GB, with a dash for unknowns', () => {
    expect(formatMemory(41 * 1024)).toBe('41.0 GB');
    expect(formatMemory(null)).toBe('-');
    expect(formatDisk(180)).toBe('180 GB');
    expect(formatDisk(undefined)).toBe('-');
  });

  it('clamps percentages into 0-100', () => {
    expect(clampPct(-5)).toBe(0);
    expect(clampPct(140)).toBe(100);
    expect(clampPct(63)).toBe(63);
    expect(clampPct(null)).toBe(0);
  });

  it('maps utilisation to a tone, acute red only past 90 (R4)', () => {
    expect(meterTone(10)).toBe('ok');
    expect(meterTone(75)).toBe('warn');
    expect(meterTone(95)).toBe('high');
  });

  it('labels every heartbeat status', () => {
    expect(hostStatusLabel('online')).toBe('Online');
    expect(hostStatusLabel('draining')).toBe('Draining');
    expect(hostStatusLabel('retired')).toBe('Retired');
  });

  it('gives acute tone only to degraded/offline; settled states stay calm', () => {
    expect(hostStatusTone('online')).toBe('ok');
    expect(hostStatusTone('idle')).toBe('idle');
    expect(hostStatusTone('degraded')).toBe('warn');
    expect(hostStatusTone('offline')).toBe('error');
    expect(hostStatusTone('draining')).toBe('idle');
    expect(hostStatusTone('retired')).toBe('calm');
  });

  it('labels host role', () => {
    expect(hostRoleLabel('local')).toBe('Local');
    expect(hostRoleLabel('remote')).toBe('Remote');
  });

  it('computes used RAM / disk percentages, null when stats missing', () => {
    // 62 total, 38 free => 24 used => ~38.7%
    expect(ramUsedPct(stats)).toBe(39);
    // 240 total, 96 free => 144 used => 60%
    expect(diskUsedPct(stats)).toBe(60);
    expect(ramUsedPct(null)).toBeNull();
    expect(diskUsedPct(undefined)).toBeNull();
  });

  it('renders relative heartbeat ages against an injected clock', () => {
    const now = Date.parse('2026-07-10T12:00:00Z');
    expect(relativeHeartbeat(null, now)).toBe('never');
    expect(relativeHeartbeat('2026-07-10T11:59:50Z', now)).toBe('just now');
    expect(relativeHeartbeat('2026-07-10T11:58:00Z', now)).toBe('2m ago');
    expect(relativeHeartbeat('2026-07-10T09:00:00Z', now)).toBe('3h ago');
    expect(relativeHeartbeat('2026-07-08T12:00:00Z', now)).toBe('2d ago');
    expect(relativeHeartbeat('not-a-date', now)).toBe('never');
  });
});
