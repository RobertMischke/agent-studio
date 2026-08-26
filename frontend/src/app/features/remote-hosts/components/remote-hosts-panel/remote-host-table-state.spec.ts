import { afterEach, describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import {
  REMOTE_HOST_TABLE_SESSION_KEY,
  RemoteHostTableState,
} from './remote-host-table-state';
import { groupPhysicalHosts } from '../../models/physical-host-group';

const HOSTS: RemoteHost[] = [
  host('runner-b', 'offline', 91, '2026-08-11T08:00:00Z', 'release-2'),
  host('runner-a', 'online', 21, '2026-08-11T10:00:00Z', 'release-1'),
];
const GROUPS = groupPhysicalHosts(HOSTS, true);

afterEach(() => window.sessionStorage.clear());

describe('RemoteHostTableState', () => {
  it('sorts every data column with a stable direction toggle', () => {
    const state = new RemoteHostTableState();
    expect(state.sort(GROUPS, item => item.id === 'runner-a' ? 1 : 3).map(item => item.id))
      .toEqual(['runner-a', 'runner-b']);

    state.selectSort('load');
    expect(state.direction()).toBe('desc');
    expect(state.sort(GROUPS, () => 0).map(item => item.id)).toEqual(['runner-b', 'runner-a']);

    state.selectSort('load');
    expect(state.direction()).toBe('asc');
    expect(state.sort(GROUPS, () => 0).map(item => item.id)).toEqual(['runner-a', 'runner-b']);
  });

  it('restores sort and expanded rows from session storage', () => {
    const state = new RemoteHostTableState();
    state.selectSort('release');
    state.setExpanded('runner-a', true);

    const restored = new RemoteHostTableState();
    restored.hydrate();

    expect(restored.sortKey()).toBe('release');
    expect(restored.isExpanded('runner-a')).toBe(true);
    expect(JSON.parse(window.sessionStorage.getItem(REMOTE_HOST_TABLE_SESSION_KEY) ?? '{}'))
      .toMatchObject({ sortKey: 'release', expandedHostIds: ['runner-a'] });
  });
});

function host(
  id: string,
  status: RemoteHost['status'],
  cpuLoadPct: number,
  lastHeartbeatAt: string,
  releaseId: string,
): RemoteHost {
  return {
    id,
    name: id,
    role: 'remote',
    address: null,
    clientId: id,
    status,
    os: 'Linux',
    lastHeartbeatAt,
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
    releaseId,
    capacityHostId: id,
  };
}
