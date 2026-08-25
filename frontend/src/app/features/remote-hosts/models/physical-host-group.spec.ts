import { describe, expect, it } from 'vitest';
import type { RemoteHost } from './remote-host.model';
import { groupPhysicalHosts } from './physical-host-group';

const coding = host('coding', 'agent-runner-01', 'coding', 'online', 31, '2026-08-12T09:00:00Z');
const review = host('review', 'agent-runner-01-review', 'review', 'online', 72, '2026-08-12T09:01:00Z');
const retired = host('old', 'e2e-retired', 'coding', 'retired', null, '2026-08-01T09:00:00Z');

describe('physical host grouping', () => {
  it('groups runner roles by advertised host identity and uses one machine sample', () => {
    const [group] = groupPhysicalHosts([review, coding], false);

    expect(group.id).toBe('host-berlin');
    expect(group.name).toBe('host-berlin');
    expect(group.roles.map(role => role.serviceRole)).toEqual(['coding', 'review']);
    expect(group.machine.stats?.cpuLoadPct).toBe(72);
    expect(group.machine.lastHeartbeatAt).toBe('2026-08-12T09:01:00Z');
    expect(group.machine.capabilities).toEqual(['executor:coding', 'executor:review']);
  });

  it('hides retired identities unless the operator reveals them', () => {
    expect(groupPhysicalHosts([coding, retired], false).map(group => group.id))
      .toEqual(['host-berlin']);
    expect(groupPhysicalHosts([coding, retired], true).map(group => group.id))
      .toEqual(['host-berlin', 'host-retired']);
  });

  it('does not infer shared machines from similar names without an advertised host id', () => {
    const legacyCoding = { ...coding, capacityHostId: null };
    const legacyReview = { ...review, capacityHostId: null };

    expect(groupPhysicalHosts([legacyCoding, legacyReview], false)).toHaveLength(2);
  });
});

function host(
  id: string,
  name: string,
  serviceRole: RemoteHost['serviceRole'],
  status: RemoteHost['status'],
  cpuLoadPct: number | null,
  lastHeartbeatAt: string,
): RemoteHost {
  return {
    id,
    name,
    role: 'remote',
    serviceRole,
    capacityHostId: id === 'old' ? 'host-retired' : 'host-berlin',
    address: null,
    clientId: id,
    status,
    os: 'Linux',
    lastHeartbeatAt,
    uptimeLabel: null,
    capabilities: [`executor:${serviceRole}`],
    cliQuotas: [],
    stats: cpuLoadPct === null ? null : {
      ramTotalMb: 1024,
      ramFreeMb: 512,
      cpuCores: 4,
      cpuModel: 'test',
      cpuLoadPct,
      diskTotalGb: 10,
      diskFreeGb: 5,
    },
  };
}
