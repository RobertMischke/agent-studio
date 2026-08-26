import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { LocalCliCapabilitySnapshot, RemoteHost } from '../models/remote-host.model';
import {
  LocalCliCapabilityService,
  projectLocalCliCapabilities,
} from './local-cli-capability.service';

const SNAPSHOT: LocalCliCapabilitySnapshot = {
  observedAt: '2026-08-18T10:15:00Z',
  capabilities: [{
    cliType: 'claude', status: 'ready', installState: 'Ready', configuredPath: 'claude',
    resolvedPath: 'C:/Users/operator/AppData/Roaming/npm/claude.cmd', version: '2.1.234',
    detail: 'claude CLI is available.', observedAt: '2026-08-18T10:15:00Z',
  }],
  latestRepair: {
    cliType: 'claude', outcome: 'repaired', occurredAt: '2026-08-18T10:14:00Z',
    versionBefore: '2.1.231', versionAfter: '2.1.234', detail: 'claude CLI repaired.',
  },
  repairAlarm: false,
};

describe('LocalCliCapabilityService', () => {
  it('hydrates the host-local repair receipt', () => {
    TestBed.configureTestingModule({
      providers: [LocalCliCapabilityService, provideHttpClient(), provideHttpClientTesting()],
    });
    const service = TestBed.inject(LocalCliCapabilityService);
    const http = TestBed.inject(HttpTestingController);

    service.refresh();
    http.expectOne('/api/cli/local-capabilities').flush(SNAPSHOT);

    expect(service.snapshot()).toEqual(SNAPSHOT);
    http.verify();
  });

  it('projects repair state only onto the local host', () => {
    const host = (id: string, role: 'local' | 'remote'): RemoteHost => ({
      id, role, name: id, address: null, clientId: id, status: 'online', os: 'test',
      lastHeartbeatAt: null, uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
    });

    const projected = projectLocalCliCapabilities(
      [host('local', 'local'), host('runner', 'remote')],
      { ...SNAPSHOT, repairAlarm: true },
    );

    expect(projected[0]).toMatchObject({
      status: 'degraded',
      localCliRepairAlarm: true,
      capabilities: ['cli-execution:claude 2.1.234'],
    });
    expect(projected[1].localCliRepair).toBeUndefined();
  });
});
