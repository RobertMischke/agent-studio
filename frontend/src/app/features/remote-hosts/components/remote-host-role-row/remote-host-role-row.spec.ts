import { describe, expect, it } from 'vitest';
import type { RemoteHost } from '../../models/remote-host.model';
import { roleSlotTotal } from './remote-host-role-row';

describe('roleSlotTotal', () => {
  it('uses the review role RUNNER_MAX_PARALLELISM advertisement', () => {
    expect(roleSlotTotal(host({ serviceRole: 'review', roleMaxParallelism: 6 }))).toBe(6);
  });

  it('uses the coding role adopted ceiling before its bootstrap value', () => {
    expect(roleSlotTotal(host({
      serviceRole: 'coding',
      roleMaxParallelism: 6,
      effectiveMaxParallelism: 4,
    }))).toBe(4);
  });

  it('keeps the centrally managed coding ceiling authoritative', () => {
    expect(roleSlotTotal(host({
      serviceRole: 'coding',
      roleMaxParallelism: 6,
      effectiveMaxParallelism: 4,
      runtimeCapacity: {
        hostId: 'runner',
        maxParallelism: 3,
        targetLoadPercent: 80,
        rampStrategy: 'balanced',
        version: 2,
        updatedAt: '2026-08-12T07:00:00Z',
      },
    }))).toBe(3);
  });
});

function host(overrides: Partial<RemoteHost>): RemoteHost {
  return {
    id: 'runner',
    name: 'runner',
    role: 'remote',
    address: null,
    clientId: 'runner',
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: new Date().toISOString(),
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: null,
    ...overrides,
  };
}
